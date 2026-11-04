using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace SSDRescue;

public sealed class MainForm : Form
{
    readonly TextBox sourceBox = new() { Dock = DockStyle.Fill };
    readonly TextBox targetBox = new() { Dock = DockStyle.Fill };
    readonly Button startButton = new() { Text = "开始 / 继续", AutoSize = true };
    readonly Button pauseButton = new() { Text = "暂停", AutoSize = true, Enabled = false };
    readonly Button rescanButton = new() { Text = "重新扫描磁盘", AutoSize = true };
    readonly Label status = new() { Text = "未开始", AutoSize = true };
    readonly Label counters = new() { Text = "", AutoSize = true };
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    readonly RichTextBox logBox = new() { Dock = DockStyle.Fill, ReadOnly = true };
    CancellationTokenSource? cts;
    readonly JobDb db;
    string? jobId;
    bool closing;

    public MainForm()
    {
        Text = "SSDRescue - 安全数据抢救";
        Width = 900;
        Height = 620;
        MinimumSize = new Size(760, 520);

        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SSDRescue");
        Directory.CreateDirectory(dataDir);
        db = new JobDb(Path.Combine(dataDir, "jobs.db"));

        var saved = db.GetLastJob();
        if (saved.HasValue)
        {
            jobId = saved.Value.Id;
            sourceBox.Text = saved.Value.Source;
            targetBox.Text = saved.Value.Target;
        }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 2,
            RowCount = 7
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(new Label { Text = "源目录", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        root.Controls.Add(sourceBox, 1, 0);
        root.Controls.Add(new Label { Text = "目标目录", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        root.Controls.Add(targetBox, 1, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        var chooseS = new Button { Text = "选择源", AutoSize = true };
        var chooseT = new Button { Text = "选择目标", AutoSize = true };
        chooseS.Click += (_, _) => Pick(sourceBox);
        chooseT.Click += (_, _) => Pick(targetBox);
        startButton.Click += async (_, _) => await StartAsync();
        pauseButton.Click += (_, _) => cts?.Cancel();
        rescanButton.Click += (_, _) => RunRescan();
        buttons.Controls.AddRange([chooseS, chooseT, startButton, pauseButton, rescanButton]);

        root.Controls.Add(buttons, 0, 2);
        root.SetColumnSpan(buttons, 2);
        root.Controls.Add(status, 0, 3);
        root.SetColumnSpan(status, 2);
        root.Controls.Add(counters, 0, 4);
        root.SetColumnSpan(counters, 2);
        root.Controls.Add(progress, 0, 5);
        root.SetColumnSpan(progress, 2);
        root.Controls.Add(logBox, 0, 6);
        root.SetColumnSpan(logBox, 2);
        Controls.Add(root);

        FormClosing += MainForm_FormClosing;
        UpdateCounters();
    }

    void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (cts is null)
            return;

        // The copy operation is cancellation-aware and checkpoints after each
        // flushed chunk. Give it a chance to close its handles before exit.
        closing = true;
        cts.Cancel();
        e.Cancel = true;
        status.Text = "正在安全停止，请稍候……";
        pauseButton.Enabled = false;
        startButton.Enabled = false;

        _ = WaitForCloseAsync();
    }

    async Task WaitForCloseAsync()
    {
        while (cts is not null)
            await Task.Delay(50);

        if (!IsDisposed)
            BeginInvoke(Close);
    }

    void Pick(TextBox box)
    {
        using var d = new FolderBrowserDialog { Description = "选择目录" };
        if (Directory.Exists(box.Text))
            d.SelectedPath = box.Text;
        if (d.ShowDialog(this) == DialogResult.OK)
            box.Text = d.SelectedPath;
    }

    async Task StartAsync()
    {
        if (cts is not null)
            return;

        string src;
        string dst;
        try
        {
            src = NormalizePath(Path.GetFullPath(sourceBox.Text.Trim()));
            dst = NormalizePath(Path.GetFullPath(targetBox.Text.Trim()));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "路径无效：" + ex.Message, "SSDRescue", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (!Directory.Exists(src))
        {
            MessageBox.Show(this, "源目录不存在。", "SSDRescue", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        uint sourceSerial;
        try
        {
            sourceSerial = VolumeIdentity.GetSerial(src);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法读取源磁盘卷标识：" + ex.Message, "SSDRescue", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "源目录和目标目录不能相同。", "SSDRescue", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (IsSubdirectory(dst, src))
        {
            MessageBox.Show(this, "为了防止递归复制，目标目录不能位于源目录内部。", "SSDRescue", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(dst);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法创建目标目录：" + ex.Message, "SSDRescue",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        jobId ??= Guid.NewGuid().ToString("N");

        // Once a job has a file list, its source/target are deliberately fixed.
        // Changing them would make the existing relative-path database refer to
        // a different disk and could cause an unsafe copy.
        var existing = db.GetJob(jobId);
        if (existing.HasValue && db.FileCount(jobId) > 0)
        {
            if (!string.Equals(NormalizePath(existing.Value.Source), src, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(NormalizePath(existing.Value.Target), dst, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "这个任务已经建立文件清单，源目录和目标目录不能更改。\r\n\r\n" +
                    "如果要处理另一组目录，请建立新的任务。当前版本为了数据安全不会自动迁移任务。",
                    "SSDRescue", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (existing.Value.SourceSerial != 0 && existing.Value.SourceSerial != sourceSerial)
            {
                MessageBox.Show(this,
                    "源目录所在磁盘的卷标识已经变化。

" +
                    "为了防止掉盘后盘符被其他磁盘重新占用，程序不会继续复制。请确认原 SSD 已重新连接，并使用原来的卷。",
                    "SSDRescue", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        else
        {
            db.UpsertJob(jobId, src, dst, sourceSerial);
        }

        cts = new CancellationTokenSource();
        startButton.Enabled = false;
        pauseButton.Enabled = true;

        try
        {
            if (db.FileCount(jobId) == 0)
            {
                Log("正在建立文件清单……");
                try
                {
                    await Task.Run(() => ScanFiles(jobId!, src, cts.Token), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    status.Text = "已暂停";
                    Log("建立文件清单已暂停。可以继续。" );
                    return;
                }
                catch (Exception ex)
                {
                    Log("建立文件清单失败：" + ex.Message);
                    return;
                }
            }

            await RunRoundsAsync(jobId!, src, dst, cts.Token);
        }
        catch (OperationCanceledException)
        {
            status.Text = "已暂停";
            Log("已暂停。任务状态已保存，可继续。" );
        }
        catch (Exception ex)
        {
            status.Text = "程序错误";
            Log("程序错误：" + ex);
        }
        finally
        {
            var old = cts;
            cts = null;
            old?.Dispose();
            pauseButton.Enabled = false;
            startButton.Enabled = !closing;
            UpdateCounters();
        }
    }

    void ScanFiles(string id, string src, CancellationToken ct)
    {
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fi = new FileInfo(f);
                if (fi.Exists)
                    db.AddFile(id, Path.GetRelativePath(src, f), fi.Length, fi.LastWriteTimeUtc.Ticks);
            }
            catch (Exception ex)
            {
                Log("清单跳过：" + f + " / " + ex.Message);
            }
        }
    }

    async Task RunRoundsAsync(string id, string src, string dst, CancellationToken ct)
    {
        for (int round = 1; round <= 1000; round++)
        {
            ct.ThrowIfCancellationRequested();
            var pending = db.PendingFiles(id);

            if (pending.Count == 0)
            {
                status.Text = "完成";
                Log("全部文件已完成并验证。" );
                return;
            }

            Log($"第 {round} 轮：待处理 {pending.Count} 个文件。" );
            bool disconnected = false;
            int completedThisRound = 0;

            foreach (var file in pending)
            {
                ct.ThrowIfCancellationRequested();
                status.Text = $"第 {round} 轮：{file.RelativePath}";

                var result = await CopyOneAsync(id, src, dst, file, ct);
                if (result == CopyResult.Disconnected)
                {
                    disconnected = true;
                    break;
                }

                if (result == CopyResult.Success)
                    completedThisRound++;

                UpdateCounters();
            }

            if (disconnected)
            {
                status.Text = "源盘疑似掉盘，已暂停";
                Log("检测到 I/O/设备异常，停止继续读取，避免反复轰击设备。请重新连接/重启后点击继续。" );
                return;
            }

            if (completedThisRound == 0 && pending.Count > 0)
            {
                Log("本轮没有新增成功文件；等待 2 秒后进入下一轮，避免连续高频重试。" );
                await Task.Delay(2000, ct);
            }
            else
            {
                await Task.Delay(200, ct);
            }
        }

        status.Text = "达到本次最大重试轮数";
        Log("达到 1000 轮重试上限。未完成文件仍保留在数据库中，可再次点击继续。" );
    }

    async Task<CopyResult> CopyOneAsync(string id, string src, string dst, JobFile f, CancellationToken ct)
    {
        var source = Path.Combine(src, f.RelativePath);
        var target = Path.Combine(dst, f.RelativePath);
        var partial = target + ".partial";

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        int attempt = db.BeginAttempt(f.Id);

        try
        {
            var fi = new FileInfo(source);
            if (!fi.Exists)
                throw new FileNotFoundException("源文件不存在", source);

            if (fi.Length != f.Length || fi.LastWriteTimeUtc.Ticks != f.LastWriteTicks)
            {
                db.Mark(f.Id, "SourceChanged", null);
                db.EndAttempt(attempt, "SourceChanged", null, "源文件大小或修改时间已改变");
                TryDeletePartial(partial);
                return CopyResult.Failed;
            }

            long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (offset > f.Length)
            {
                TryDeletePartial(partial);
                offset = 0;
            }

            // If the database checkpoint is behind the actual .partial length,
            // discard the uncheckpointed tail. This makes restart recovery safe.
            offset = Math.Min(offset, Math.Max(0, f.CheckpointOffset));

            db.MarkCopying(f.Id, offset);

            // Important: these streams are scoped only to the actual copy.
            // They MUST be disposed before hashing or renaming the .partial file.
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                       1024 * 1024, FileOptions.SequentialScan))
            using (var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                       1024 * 1024, FileOptions.SequentialScan))
            {
                input.Position = offset;
                output.Position = offset;
                output.SetLength(offset);

                var buffer = new byte[4 * 1024 * 1024];
                long done = offset;

                while (done < f.Length)
                {
                    ct.ThrowIfCancellationRequested();

                    int want = (int)Math.Min(buffer.Length, f.Length - done);
                    int n = await input.ReadAsync(buffer.AsMemory(0, want), ct);
                    if (n <= 0)
                        throw new EndOfStreamException("源文件提前结束");

                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    output.Flush(true);

                    done += n;
                    db.Checkpoint(f.Id, done);
                }
            }

            // From this point onward the .partial file has no open handle from
            // the copy phase. Hashing and Move are therefore safe on Windows.
            ct.ThrowIfCancellationRequested();
            string destinationHash = await HashFileAsync(partial, ct);

            var fi2 = new FileInfo(source);
            if (!fi2.Exists || fi2.Length != f.Length || fi2.LastWriteTimeUtc.Ticks != f.LastWriteTicks)
            {
                db.Mark(f.Id, "SourceChanged", null);
                db.EndAttempt(attempt, "SourceChanged", null, "复制后源文件大小或修改时间已改变");
                TryDeletePartial(partial);
                return CopyResult.Failed;
            }

            if (new FileInfo(partial).Length != f.Length)
                throw new IOException("目标长度校验失败");

            // A second hash of the source makes the verification cryptographic:
            // destinationHash must match the source bytes read after the copy.
            string sourceHash = await HashFileAsync(source, ct);
            if (!string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
            {
                // A full .partial with a hash mismatch is not safe to resume.
                // Discard it so the next attempt really rereads the source.
                TryDeletePartial(partial);
                db.ResetCheckpoint(f.Id);
                throw new IOException($"SHA-256 校验失败：源={sourceHash} 目标={destinationHash}");
            }

            // The source metadata was checked again after the cryptographic read.
            var fi3 = new FileInfo(source);
            if (!fi3.Exists || fi3.Length != f.Length || fi3.LastWriteTimeUtc.Ticks != f.LastWriteTicks)
            {
                db.Mark(f.Id, "SourceChanged", null);
                db.EndAttempt(attempt, "SourceChanged", null, "SHA-256 校验期间源文件发生变化");
                TryDeletePartial(partial);
                return CopyResult.Failed;
            }

            // The old destination is removed only after the new data has passed
            // size + SHA-256 + source-stability checks.
            if (File.Exists(target))
                File.Delete(target);

            File.Move(partial, target);
            File.SetLastWriteTimeUtc(target, fi3.LastWriteTimeUtc);

            db.Mark(f.Id, "Success", destinationHash);
            db.EndAttempt(attempt, "Success", destinationHash, null);
            return CopyResult.Success;
        }
        catch (OperationCanceledException)
        {
            db.EndAttempt(attempt, "Paused", null, "用户暂停");
            throw;
        }
        catch (IOException ex)
        {
            db.Mark(f.Id, "Failed", null);
            db.EndAttempt(attempt, "Failed", null, ex.Message);
            Log($"失败：{f.RelativePath} / {ex.Message}");
            return IsDeviceGone(src) ? CopyResult.Disconnected : CopyResult.Failed;
        }
        catch (UnauthorizedAccessException ex)
        {
            db.Mark(f.Id, "Failed", null);
            db.EndAttempt(attempt, "Failed", null, ex.Message);
            Log($"访问失败：{f.RelativePath} / {ex.Message}");
            return CopyResult.Failed;
        }
    }

    static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        var h = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(h);
    }

    static bool IsDeviceGone(string sourceRoot)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(sourceRoot))!;
            return !Directory.Exists(root);
        }
        catch
        {
            return true;
        }
    }

    static string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Length == 0 ? Path.DirectorySeparatorChar.ToString() : full;
    }

    static bool IsSubdirectory(string candidate, string parent)
    {
        string c = NormalizePath(candidate) + Path.DirectorySeparatorChar;
        string p = NormalizePath(parent) + Path.DirectorySeparatorChar;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Never let cleanup failure mask the source-safety logic.
        }
    }

    void UpdateCounters()
    {
        if (jobId is null)
            return;

        var c = db.Counts(jobId);
        counters.Text = $"总计 {c.total} | 已验证 {c.ok} | 待处理 {c.pending} | 失败 {c.failed} | 源文件变化 {c.changed}";
        progress.Value = c.total == 0 ? 0 : Math.Clamp((int)(100.0 * c.ok / c.total), 0, 100);
    }

    void RunRescan()
    {
        try
        {
            var psi = new ProcessStartInfo("diskpart.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi)!;
            p.StandardInput.WriteLine("rescan");
            p.StandardInput.WriteLine("exit");
            p.StandardInput.Close();

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            Log(output);
            status.Text = "已请求 Windows 重新扫描磁盘。";
        }
        catch (Exception ex)
        {
            Log("重新扫描失败：" + ex.Message);
        }
    }

    void Log(string s)
    {
        if (InvokeRequired)
        {
            if (!IsDisposed)
                BeginInvoke(() => Log(s));
            return;
        }

        logBox.AppendText($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}\r\n");
    }
}

internal static class VolumeIdentity
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    public static uint GetSerial(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new IOException("无法确定源目录所在磁盘");
        if (!GetVolumeInformation(root, null, 0, out uint serial, out _, out _, null, 0))
            throw new IOException($"GetVolumeInformation 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
        return serial;
    }
}

public enum CopyResult
{
    Success,
    Failed,
    Disconnected
}

public sealed record JobFile(long Id, string RelativePath, long Length, long LastWriteTicks, long CheckpointOffset);

public sealed class JobDb
{
    readonly string cs;

    public JobDb(string path)
    {
        cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default
        }.ToString();

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS jobs(
    id TEXT PRIMARY KEY,
    source TEXT NOT NULL,
    target TEXT NOT NULL,
    source_serial INTEGER NOT NULL DEFAULT 0,
    created TEXT NOT NULL,
    updated TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS files(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id TEXT NOT NULL,
    rel TEXT NOT NULL,
    length INTEGER NOT NULL,
    lastwrite INTEGER NOT NULL,
    status TEXT NOT NULL DEFAULT 'Pending',
    offset INTEGER NOT NULL DEFAULT 0,
    sha256 TEXT,
    attempts INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(job_id) REFERENCES jobs(id),
    UNIQUE(job_id,rel)
);
CREATE TABLE IF NOT EXISTS attempts(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id INTEGER NOT NULL,
    started TEXT NOT NULL,
    ended TEXT,
    status TEXT,
    sha256 TEXT,
    error TEXT
);
";
        cmd.ExecuteNonQuery();

        // Safe schema upgrade for jobs created by the previous test build.
        using (var checkColumn = c.CreateCommand())
        {
            checkColumn.CommandText = "PRAGMA table_info(jobs)";
            bool hasSerial = false;
            using var reader = checkColumn.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "source_serial", StringComparison.OrdinalIgnoreCase))
                {
                    hasSerial = true;
                    break;
                }
            }

            if (!hasSerial)
            {
                using var alterColumn = c.CreateCommand();
                alterColumn.CommandText = "ALTER TABLE jobs ADD COLUMN source_serial INTEGER NOT NULL DEFAULT 0";
                alterColumn.ExecuteNonQuery();
            }
        }

        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA user_version";
        int version = Convert.ToInt32(pragma.ExecuteScalar());
        if (version < 1)
        {
            using var tx = c.BeginTransaction();
            using var alter = c.CreateCommand();
            alter.Transaction = tx;
            alter.CommandText = "PRAGMA user_version = 1";
            alter.ExecuteNonQuery();
            tx.Commit();
        }
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(cs);
        c.Open();
        return c;
    }

    public void UpsertJob(string id, string source, string target, uint sourceSerial)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO jobs(id,source,target,source_serial,created,updated)
VALUES($i,$s,$t,$v,$n,$n)
ON CONFLICT(id) DO UPDATE SET
    source=$s,
    target=$t,
    source_serial=$v,
    updated=$n";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$t", target);
        cmd.Parameters.AddWithValue("$v", sourceSerial);
        cmd.Parameters.AddWithValue("$n", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public (string Id, string Source, string Target, uint SourceSerial)? GetLastJob()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,source,target,source_serial FROM jobs ORDER BY updated DESC LIMIT 1";
        using var r = cmd.ExecuteReader();

        if (!r.Read())
            return null;

        return (r.GetString(0), r.GetString(1), r.GetString(2), Convert.ToUInt32(r.GetInt64(3)));
    }

    public (string Id, string Source, string Target, uint SourceSerial)? GetJob(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,source,target,source_serial FROM jobs WHERE id=$i LIMIT 1";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;
        return (r.GetString(0), r.GetString(1), r.GetString(2), Convert.ToUInt32(r.GetInt64(3)));
    }

    public int FileCount(string job)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files WHERE job_id=$j";
        cmd.Parameters.AddWithValue("$j", job);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void AddFile(string job, string rel, long len, long ticks)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT OR IGNORE INTO files(job_id,rel,length,lastwrite)
VALUES($j,$r,$l,$t)";
        cmd.Parameters.AddWithValue("$j", job);
        cmd.Parameters.AddWithValue("$r", rel);
        cmd.Parameters.AddWithValue("$l", len);
        cmd.Parameters.AddWithValue("$t", ticks);
        cmd.ExecuteNonQuery();
    }

    public List<JobFile> PendingFiles(string job)
    {
        var a = new List<JobFile>();

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT id,rel,length,lastwrite,offset
FROM files
WHERE job_id=$j AND status NOT IN ('Success','SourceChanged')
ORDER BY id";
        cmd.Parameters.AddWithValue("$j", job);

        using var r = cmd.ExecuteReader();
        while (r.Read())
            a.Add(new JobFile(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));

        return a;
    }

    public int BeginAttempt(long file)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        using var u = c.CreateCommand();
        u.Transaction = tx;
        u.CommandText = "UPDATE files SET attempts=attempts+1,status='Copying' WHERE id=$i";
        u.Parameters.AddWithValue("$i", file);
        u.ExecuteNonQuery();

        using var i = c.CreateCommand();
        i.Transaction = tx;
        i.CommandText = "INSERT INTO attempts(file_id,started) VALUES($i,$n); SELECT last_insert_rowid();";
        i.Parameters.AddWithValue("$i", file);
        i.Parameters.AddWithValue("$n", DateTime.UtcNow.ToString("O"));

        var id = Convert.ToInt32(i.ExecuteScalar());
        tx.Commit();
        return id;
    }

    public void Checkpoint(long file, long offset)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE files SET offset=$o,status='Copying' WHERE id=$i";
        cmd.Parameters.AddWithValue("$o", offset);
        cmd.Parameters.AddWithValue("$i", file);
        cmd.ExecuteNonQuery();
    }

    public void MarkCopying(long file, long offset)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE files SET status='Copying',offset=$o WHERE id=$i";
        cmd.Parameters.AddWithValue("$o", offset);
        cmd.Parameters.AddWithValue("$i", file);
        cmd.ExecuteNonQuery();
    }

    public void ResetCheckpoint(long file)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE files SET offset=0,status='Failed',sha256=NULL WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", file);
        cmd.ExecuteNonQuery();
    }

    public void Mark(long file, string status, string? hash)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE files SET status=$s,sha256=$h WHERE id=$i";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$h", (object?)hash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$i", file);
        cmd.ExecuteNonQuery();
    }

    public void EndAttempt(long id, string status, string? hash, string? error)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
UPDATE attempts
SET ended=$n,status=$s,sha256=$h,error=$e
WHERE id=$i";
        cmd.Parameters.AddWithValue("$n", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$h", (object?)hash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }

    public (int total, int ok, int pending, int failed, int changed) Counts(string job)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT
    COUNT(*),
    SUM(CASE WHEN status='Success' THEN 1 ELSE 0 END),
    SUM(CASE WHEN status NOT IN ('Success','SourceChanged') THEN 1 ELSE 0 END),
    SUM(CASE WHEN status='Failed' THEN 1 ELSE 0 END),
    SUM(CASE WHEN status='SourceChanged' THEN 1 ELSE 0 END)
FROM files
WHERE job_id=$j";
        cmd.Parameters.AddWithValue("$j", job);

        using var r = cmd.ExecuteReader();
        r.Read();

        return (
            r.GetInt32(0),
            Convert.ToInt32(r.IsDBNull(1) ? 0 : r.GetInt64(1)),
            Convert.ToInt32(r.IsDBNull(2) ? 0 : r.GetInt64(2)),
            Convert.ToInt32(r.IsDBNull(3) ? 0 : r.GetInt64(3)),
            Convert.ToInt32(r.IsDBNull(4) ? 0 : r.GetInt64(4))
        );
    }
}
