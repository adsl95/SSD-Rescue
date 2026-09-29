using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SSDRescue;

public sealed class MainForm : Form
{
    readonly TextBox sourceBox = new() { Dock = DockStyle.Fill };
    readonly TextBox targetBox = new() { Dock = DockStyle.Fill };
    readonly Button startButton = new() { Text = "开始 / 继续", AutoSize = true };
    readonly Button pauseButton = new() { Text = "暂停", AutoSize = true };
    readonly Button rescanButton = new() { Text = "重新扫描磁盘", AutoSize = true };
    readonly Label status = new() { Text = "未开始", AutoSize = true };
    readonly Label counters = new() { Text = "", AutoSize = true };
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    readonly RichTextBox logBox = new() { Dock = DockStyle.Fill, ReadOnly = true };
    CancellationTokenSource? cts;
    readonly JobDb db;
    string? jobId;

    public MainForm()
    {
        Text = "SSDRescue - 安全数据抢救";
        Width = 900; Height = 620;
        MinimumSize = new Size(760, 520);
        db = new JobDb(Path.Combine(AppContext.BaseDirectory, "jobs.db"));
        var saved = db.GetLastJob();
        if (saved is not null) { jobId = saved.Id; sourceBox.Text = saved.Source; targetBox.Text = saved.Target; }

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 2, RowCount = 7 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(new Label { Text = "源目录", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        root.Controls.Add(sourceBox, 1, 0);
        root.Controls.Add(new Label { Text = "目标目录", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        root.Controls.Add(targetBox, 1, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        var chooseS = new Button { Text = "选择源", AutoSize = true }; chooseS.Click += (_,_) => Pick(sourceBox);
        var chooseT = new Button { Text = "选择目标", AutoSize = true }; chooseT.Click += (_,_) => Pick(targetBox);
        startButton.Click += async (_,_) => await StartAsync();
        pauseButton.Click += (_,_) => cts?.Cancel();
        rescanButton.Click += (_,_) => RunRescan();
        buttons.Controls.AddRange([chooseS, chooseT, startButton, pauseButton, rescanButton]);
        root.Controls.Add(buttons, 0, 2); root.SetColumnSpan(buttons, 2);
        root.Controls.Add(status, 0, 3); root.SetColumnSpan(status, 2);
        root.Controls.Add(counters, 0, 4); root.SetColumnSpan(counters, 2);
        root.Controls.Add(progress, 0, 5); root.SetColumnSpan(progress, 2);
        root.Controls.Add(logBox, 0, 6); root.SetColumnSpan(logBox, 2);
        Controls.Add(root);
        FormClosing += (_,_) => cts?.Cancel();
    }

    void Pick(TextBox box)
    {
        using var d = new FolderBrowserDialog { Description = "选择目录" };
        if (Directory.Exists(box.Text)) d.SelectedPath = box.Text;
        if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.SelectedPath;
    }

    async Task StartAsync()
    {
        if (cts is not null) return;
        var src = Path.GetFullPath(sourceBox.Text.Trim());
        var dst = Path.GetFullPath(targetBox.Text.Trim());
        if (!Directory.Exists(src)) { MessageBox.Show("源目录不存在。"); return; }
        if (string.Equals(src.TrimEnd('\\'), dst.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { MessageBox.Show("源目录和目标目录不能相同。"); return; }
        Directory.CreateDirectory(dst);
        jobId ??= Guid.NewGuid().ToString("N");
        db.UpsertJob(jobId, src, dst);
        if (db.FileCount(jobId) == 0) { Log("正在建立文件清单……"); await Task.Run(() => ScanFiles(jobId, src)); }
        cts = new CancellationTokenSource();
        try { await RunRoundsAsync(jobId, src, dst, cts.Token); }
        catch (OperationCanceledException) { Log("已暂停。任务状态已保存，可继续。"); }
        catch (Exception ex) { Log("程序错误：" + ex); }
        finally { cts.Dispose(); cts = null; UpdateCounters(); }
    }

    void ScanFiles(string id, string src)
    {
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            try { var fi = new FileInfo(f); db.AddFile(id, Path.GetRelativePath(src, f), fi.Length, fi.LastWriteTimeUtc.Ticks); }
            catch (Exception ex) { Log("清单跳过：" + f + " / " + ex.Message); }
        }
    }

    async Task RunRoundsAsync(string id, string src, string dst, CancellationToken ct)
    {
        for (int round = 1; round <= 1000; round++)
        {
            ct.ThrowIfCancellationRequested();
            var pending = db.PendingFiles(id);
            if (pending.Count == 0) { Log("全部文件已完成并验证。"); status.Text = "完成"; return; }
            Log($"第 {round} 轮：待处理 {pending.Count} 个文件。");
            bool disconnected = false;
            foreach (var file in pending)
            {
                ct.ThrowIfCancellationRequested();
                status.Text = $"第 {round} 轮：{file.RelativePath}";
                var result = await CopyOneAsync(id, src, dst, file, ct);
                if (result == CopyResult.Disconnected) { disconnected = true; break; }
                UpdateCounters();
            }
            if (disconnected)
            {
                status.Text = "源盘疑似掉盘，已暂停。请重新连接/重启后点击继续。";
                Log("检测到 I/O/设备异常，停止继续读取，避免反复轰击设备。");
                return;
            }
        }
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
            if (!fi.Exists) throw new FileNotFoundException("源文件不存在", source);
            if (fi.Length != f.Length || fi.LastWriteTimeUtc.Ticks != f.LastWriteTicks)
            { db.Mark(f.Id, "SourceChanged", null); return CopyResult.Failed; }
            long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (offset > f.Length) { File.Delete(partial); offset = 0; }
            db.MarkCopying(f.Id, offset);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            using var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            input.Position = offset; output.Position = offset; output.SetLength(offset);
            var buffer = new byte[4 * 1024 * 1024];
            long done = offset;
            while (done < f.Length)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, f.Length - done);
                int n = await input.ReadAsync(buffer.AsMemory(0, want), ct);
                if (n <= 0) throw new EndOfStreamException("源文件提前结束");
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                await output.FlushAsync(ct);
                done += n;
                db.Checkpoint(f.Id, done);
            }
            output.Flush(true);
            string hash = await HashFileAsync(partial, ct);
            var fi2 = new FileInfo(source);
            if (fi2.Length != f.Length || fi2.LastWriteTimeUtc.Ticks != f.LastWriteTicks)
            { db.Mark(f.Id, "SourceChanged", hash); return CopyResult.Failed; }
            if (new FileInfo(partial).Length != f.Length) throw new IOException("目标长度校验失败");
            if (File.Exists(target)) File.Delete(target);
            File.Move(partial, target);
            File.SetLastWriteTimeUtc(target, fi.LastWriteTimeUtc);
            db.Mark(f.Id, "Success", hash);
            db.EndAttempt(attempt, "Success", hash, null);
            return CopyResult.Success;
        }
        catch (OperationCanceledException) { db.EndAttempt(attempt, "Paused", null, "用户暂停"); throw; }
        catch (IOException ex)
        {
            db.Mark(f.Id, "Failed", null); db.EndAttempt(attempt, "Failed", null, ex.Message);
            Log($"失败：{f.RelativePath} / {ex.Message}");
            return IsDeviceGone(src) ? CopyResult.Disconnected : CopyResult.Failed;
        }
        catch (UnauthorizedAccessException ex)
        { db.Mark(f.Id, "Failed", null); db.EndAttempt(attempt, "Failed", null, ex.Message); return CopyResult.Failed; }
    }

    static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create(); using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var h = await sha.ComputeHashAsync(fs, ct); return Convert.ToHexString(h);
    }

    static bool IsDeviceGone(string sourceRoot)
    {
        try { return !Directory.Exists(Path.GetPathRoot(Path.GetFullPath(sourceRoot))!); } catch { return true; }
    }

    void UpdateCounters()
    {
        if (jobId is null) return; var c = db.Counts(jobId); counters.Text = $"总计 {c.total} | 已验证 {c.ok} | 待处理 {c.pending} | 失败 {c.failed} | 源文件变化 {c.changed}"; progress.Value = c.total == 0 ? 0 : Math.Clamp((int)(100.0 * c.ok / c.total), 0, 100);
    }

    void RunRescan()
    {
        try
        {
            var psi = new ProcessStartInfo("diskpart.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!; p.StandardInput.WriteLine("rescan"); p.StandardInput.WriteLine("exit"); p.StandardInput.Close(); var output = p.StandardOutput.ReadToEnd(); p.WaitForExit(); Log(output); status.Text = "已请求 Windows 重新扫描磁盘。";
        }
        catch (Exception ex) { Log("重新扫描失败：" + ex.Message); }
    }

    void Log(string s) { if (InvokeRequired) { BeginInvoke(() => Log(s)); return; } logBox.AppendText($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}\r\n"); }
}

public enum CopyResult { Success, Failed, Disconnected }

public sealed record JobFile(long Id, string RelativePath, long Length, long LastWriteTicks);

public sealed class JobDb
{
    readonly string cs;
    public JobDb(string path) { cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString(); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, source TEXT NOT NULL, target TEXT NOT NULL, created TEXT NOT NULL, updated TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS files(id INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL, rel TEXT NOT NULL, length INTEGER NOT NULL, lastwrite INTEGER NOT NULL, status TEXT NOT NULL DEFAULT 'Pending', offset INTEGER NOT NULL DEFAULT 0, sha256 TEXT, attempts INTEGER NOT NULL DEFAULT 0, FOREIGN KEY(job_id) REFERENCES jobs(id), UNIQUE(job_id,rel));
CREATE TABLE IF NOT EXISTS attempts(id INTEGER PRIMARY KEY AUTOINCREMENT, file_id INTEGER NOT NULL, started TEXT NOT NULL, ended TEXT, status TEXT, sha256 TEXT, error TEXT);
"; cmd.ExecuteNonQuery(); }
    SqliteConnection Open() { var c = new SqliteConnection(cs); c.Open(); return c; }
    public void UpsertJob(string id,string source,string target){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO jobs(id,source,target,created,updated) VALUES($i,$s,$t,$n,$n) ON CONFLICT(id) DO UPDATE SET source=$s,target=$t,updated=$n";cmd.Parameters.AddWithValue("$i",id);cmd.Parameters.AddWithValue("$s",source);cmd.Parameters.AddWithValue("$t",target);cmd.Parameters.AddWithValue("$n",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}
    public (string Id,string Source,string Target)? GetLastJob(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT id,source,target FROM jobs ORDER BY updated DESC LIMIT 1";using var r=cmd.ExecuteReader();return r.Read()? (r.GetString(0),r.GetString(1),r.GetString(2)):null;}
    public int FileCount(string job){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM files WHERE job_id=$j";cmd.Parameters.AddWithValue("$j",job);return Convert.ToInt32(cmd.ExecuteScalar());}
    public void AddFile(string job,string rel,long len,long ticks){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT OR IGNORE INTO files(job_id,rel,length,lastwrite) VALUES($j,$r,$l,$t)";cmd.Parameters.AddWithValue("$j",job);cmd.Parameters.AddWithValue("$r",rel);cmd.Parameters.AddWithValue("$l",len);cmd.Parameters.AddWithValue("$t",ticks);cmd.ExecuteNonQuery();}
    public List<JobFile> PendingFiles(string job){var a=new List<JobFile>();using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT id,rel,length,lastwrite FROM files WHERE job_id=$j AND status NOT IN ('Success','SourceChanged') ORDER BY id";cmd.Parameters.AddWithValue("$j",job);using var r=cmd.ExecuteReader();while(r.Read())a.Add(new JobFile(r.GetInt64(0),r.GetString(1),r.GetInt64(2),r.GetInt64(3)));return a;}
    public int BeginAttempt(long file){using var c=Open();using var tx=c.BeginTransaction();using var u=c.CreateCommand();u.Transaction=tx;u.CommandText="UPDATE files SET attempts=attempts+1,status='Copying' WHERE id=$i";u.Parameters.AddWithValue("$i",file);u.ExecuteNonQuery();using var i=c.CreateCommand();i.Transaction=tx;i.CommandText="INSERT INTO attempts(file_id,started) VALUES($i,$n); SELECT last_insert_rowid();";i.Parameters.AddWithValue("$i",file);i.Parameters.AddWithValue("$n",DateTime.UtcNow.ToString("O"));var id=Convert.ToInt32(i.ExecuteScalar());tx.Commit();return id;}
    public void Checkpoint(long file,long offset){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE files SET offset=$o WHERE id=$i";cmd.Parameters.AddWithValue("$o",offset);cmd.Parameters.AddWithValue("$i",file);cmd.ExecuteNonQuery();}
    public void MarkCopying(long file,long offset){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE files SET status='Copying',offset=$o WHERE id=$i";cmd.Parameters.AddWithValue("$o",offset);cmd.Parameters.AddWithValue("$i",file);cmd.ExecuteNonQuery();}
    public void Mark(long file,string status,string? hash){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE files SET status=$s,sha256=$h WHERE id=$i";cmd.Parameters.AddWithValue("$s",status);cmd.Parameters.AddWithValue("$h",(object?)hash??DBNull.Value);cmd.Parameters.AddWithValue("$i",file);cmd.ExecuteNonQuery();}
    public void EndAttempt(long id,string status,string? hash,string? error){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE attempts SET ended=$n,status=$s,sha256=$h,error=$e WHERE id=$i";cmd.Parameters.AddWithValue("$n",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$s",status);cmd.Parameters.AddWithValue("$h",(object?)hash??DBNull.Value);cmd.Parameters.AddWithValue("$e",(object?)error??DBNull.Value);cmd.Parameters.AddWithValue("$i",id);cmd.ExecuteNonQuery();}
    public (int total,int ok,int pending,int failed,int changed) Counts(string job){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*),SUM(CASE WHEN status='Success' THEN 1 ELSE 0 END),SUM(CASE WHEN status NOT IN ('Success','SourceChanged') THEN 1 ELSE 0 END),SUM(CASE WHEN status='Failed' THEN 1 ELSE 0 END),SUM(CASE WHEN status='SourceChanged' THEN 1 ELSE 0 END) FROM files WHERE job_id=$j";cmd.Parameters.AddWithValue("$j",job);using var r=cmd.ExecuteReader();r.Read();return(r.GetInt32(0),Convert.ToInt32(r.IsDBNull(1)?0:r.GetInt64(1)),Convert.ToInt32(r.IsDBNull(2)?0:r.GetInt64(2)),Convert.ToInt32(r.IsDBNull(3)?0:r.GetInt64(3)),Convert.ToInt32(r.IsDBNull(4)?0:r.GetInt64(4)));}
}
