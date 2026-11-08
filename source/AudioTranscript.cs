using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            using (var window = new Transcriber())
            {
                if (args.Length == 2 && args[0] == "--self-test")
                {
                    window.CheckLayout(args[1]);
                    return 0;
                }
                window.AddFiles(args);
                Application.Run(window);
            }
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "АудиоТранскрипт", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed class Transcriber : Form
{
    private readonly ListView files = new ListView();
    private readonly Button add = new Button(), remove = new Button(), clear = new Button();
    private readonly Button choose = new Button(), reset = new Button(), start = new Button(), stop = new Button(), open = new Button();
    private readonly TextBox destination = new TextBox(), terms = new TextBox(), log = new TextBox();
    private readonly ComboBox language = new ComboBox();
    private readonly ProgressBar progress = new ProgressBar();
    private readonly Label status = new Label();
    private readonly JavaScriptSerializer json = new JavaScriptSerializer();
    private readonly HashSet<string> resultFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder errors = new StringBuilder();
    private Process worker;
    private string outputFolder = "", requestPath = "";
    private bool running, cancelled, terminalEvent;
    private int activeIndex = -1;
    private int runId;
    private const string DefaultDestination = "Рядом с каждой записью → папка «Транскрипты»";

    public Transcriber()
    {
        Text = "АудиоТранскрипт — локальное распознавание";
        Font = new Font("Segoe UI", 10F);
        ClientSize = new Size(920, 720);
        MinimumSize = new Size(850, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(247, 249, 252);
        AutoScaleMode = AutoScaleMode.Dpi;
        AllowDrop = true;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 13 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        float[] heights = { 38, 34, 40, 170, 28, 40, 32, 40, 32, 28, 34, 100, 52 };
        for (int i = 0; i < heights.Length; i++)
            layout.RowStyles.Add(new RowStyle(i == 3 || i == 11 ? SizeType.Percent : SizeType.Absolute,
                                             i == 3 ? 62 : i == 11 ? 38 : heights[i]));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Запись → готовый транскрипт", Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 18F, FontStyle.Bold), AutoSize = false }, 0, 0);
        layout.Controls.Add(new Label { Text = "Выберите записи или перетащите их в окно. Распознавание работает без интернета.",
            Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        Setup(add, "Добавить записи…", 170, delegate { SelectFiles(); });
        Setup(remove, "Убрать выбранные", 170, delegate {
            foreach (ListViewItem item in files.SelectedItems.Cast<ListViewItem>().ToArray()) files.Items.Remove(item);
            UpdateButtons();
        });
        Setup(clear, "Очистить список", 155, delegate { files.Items.Clear(); UpdateButtons(); });
        actions.Controls.AddRange(new Control[] { add, remove, clear });
        layout.Controls.Add(actions, 0, 2);

        files.Dock = DockStyle.Fill; files.View = View.Details; files.FullRowSelect = true;
        files.HideSelection = false; files.ShowItemToolTips = true; files.AllowDrop = true;
        files.Columns.Add("Аудио / видео", 610); files.Columns.Add("Состояние", 190);
        files.Resize += delegate { files.Columns[0].Width = Math.Max(300, files.ClientSize.Width - 195); };
        files.SelectedIndexChanged += delegate { UpdateButtons(); };
        layout.Controls.Add(files, 0, 3);
        layout.Controls.Add(new Label { Text = "Куда сохранить TXT и SRT", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 4);

        var folder = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        folder.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folder.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115)); folder.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        destination.ReadOnly = true; destination.Text = DefaultDestination; destination.Dock = DockStyle.Fill;
        Setup(choose, "Выбрать…", 105, delegate {
            using (var dialog = new FolderBrowserDialog { Description = "Папка для транскриптов", ShowNewFolderButton = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) { outputFolder = dialog.SelectedPath; destination.Text = outputFolder; }
        });
        Setup(reset, "По умолч.", 95, delegate { outputFolder = ""; destination.Text = DefaultDestination; });
        folder.Controls.Add(destination, 0, 0); folder.Controls.Add(choose, 1, 0); folder.Controls.Add(reset, 2, 0);
        layout.Controls.Add(folder, 0, 5);

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        options.Controls.Add(new Label { Text = "Язык записи:", AutoSize = true, Padding = new Padding(0, 4, 8, 0) });
        language.DropDownStyle = ComboBoxStyle.DropDownList; language.Width = 210;
        language.Items.AddRange(new object[] { "Русский", "Определить автоматически", "Английский" }); language.SelectedIndex = 0;
        options.Controls.Add(language); layout.Controls.Add(options, 0, 6);
        var hint = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        hint.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 205)); hint.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hint.Controls.Add(new Label { Text = "Имена и термины (необяз.):", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        terms.Dock = DockStyle.Fill; terms.MaxLength = 1000;
        new ToolTip().SetToolTip(terms, "Например: ВБК, оферта, УКЭП, Полина. Это подсказка распознаванию, а не команда.");
        hint.Controls.Add(terms, 1, 0); layout.Controls.Add(hint, 0, 7);
        status.Text = "Добавьте одну или несколько записей."; status.Dock = DockStyle.Fill; status.AutoEllipsis = true;
        layout.Controls.Add(status, 0, 8);
        progress.Dock = DockStyle.Fill; progress.Margin = new Padding(3, 3, 3, 10); progress.Maximum = 1000;
        layout.Controls.Add(progress, 0, 9);
        layout.Controls.Add(new Label { Text = "Ход обработки · распознавание может занять время, сопоставимое с длительностью записи",
            Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 10);
        log.Dock = DockStyle.Fill; log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical;
        log.BackColor = Color.White; layout.Controls.Add(log, 0, 11);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
        Setup(start, "Создать транскрипты", 225, delegate { StartRun(); });
        start.BackColor = Color.FromArgb(31, 92, 165); start.ForeColor = Color.White; start.FlatStyle = FlatStyle.Flat;
        Setup(stop, "Остановить", 135, delegate { CancelRun(); });
        Setup(open, "Открыть результаты", 195, delegate { OpenResults(); });
        bottom.Controls.AddRange(new Control[] { start, stop, open }); layout.Controls.Add(bottom, 0, 12);
        DragEnter += AcceptDrag; DragDrop += ReceiveDrop; files.DragEnter += AcceptDrag; files.DragDrop += ReceiveDrop;
        FormClosing += delegate { if (running) CancelRun(); DeleteRequest(); };
        UpdateButtons();
    }

    private static void Setup(Button button, string text, int width, EventHandler handler)
    {
        button.Text = text; button.Width = width; button.Height = 32; button.Click += handler;
        button.Margin = new Padding(0, 0, 9, 0);
    }
    private void AcceptDrag(object sender, DragEventArgs e)
    {
        e.Effect = !running && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }
    private void ReceiveDrop(object sender, DragEventArgs e)
    {
        if (!running && e.Data.GetDataPresent(DataFormats.FileDrop)) AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));
    }
    public void AddFiles(IEnumerable<string> paths)
    {
        if (running) return;
        foreach (string path in paths)
        {
            if (!File.Exists(path)) continue;
            string full = Path.GetFullPath(path);
            if (files.Items.Cast<ListViewItem>().Any(item => String.Equals((string)item.Tag, full, StringComparison.OrdinalIgnoreCase))) continue;
            var itemNew = new ListViewItem(Path.GetFileName(full)) { Tag = full, ToolTipText = full };
            itemNew.SubItems.Add("В очереди"); files.Items.Add(itemNew);
        }
        status.Text = files.Items.Count == 0 ? "Добавьте записи." : "Записей в списке: " + files.Items.Count;
        UpdateButtons();
    }
    private void SelectFiles()
    {
        using (var dialog = new OpenFileDialog { Multiselect = true, Title = "Выберите аудио или видео",
            Filter = "Аудио и видео|*.mp3;*.wav;*.m4a;*.mp4;*.mpeg;*.mpga;*.ogg;*.opus;*.flac;*.webm;*.aac;*.wma;*.mkv;*.mov|Все файлы|*.*" })
            if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles(dialog.FileNames);
    }
    private void UpdateButtons()
    {
        add.Enabled = clear.Enabled = choose.Enabled = reset.Enabled = language.Enabled = terms.Enabled = !running;
        remove.Enabled = !running && files.SelectedItems.Count > 0;
        start.Enabled = !running && files.Items.Count > 0; stop.Enabled = running;
        open.Enabled = resultFolders.Count > 0;
    }
    private void AppendLog(string text)
    {
        log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
    }
    private void Dispatch(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    private void StartRun()
    {
        string root = Path.GetDirectoryName(typeof(Transcriber).Assembly.Location);
        string engine = Path.Combine(root, "engine", "engine.exe"), model = Path.Combine(root, "model");
        if (!File.Exists(engine) || !File.Exists(Path.Combine(model, "model.bin")))
        {
            MessageBox.Show(this, "Рядом с программой должны находиться папки engine и model. Перемещайте папку AudioTranscript целиком.",
                "Не найдены компоненты", MessageBoxButtons.OK, MessageBoxIcon.Error); return;
        }
        try
        {
            var request = new Dictionary<string, object> { { "files", files.Items.Cast<ListViewItem>().Select(i => (string)i.Tag).ToArray() },
                { "model", model }, { "output", outputFolder }, { "prompt", terms.Text },
                { "language", language.SelectedIndex == 1 ? null : language.SelectedIndex == 2 ? "en" : "ru" } };
            requestPath = Path.Combine(Path.GetTempPath(), "AudioTranscript-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(requestPath, json.Serialize(request), new UTF8Encoding(false));
            running = true; cancelled = false; terminalEvent = false; activeIndex = -1; errors.Clear();
            progress.Style = ProgressBarStyle.Marquee; progress.Value = 0;
            foreach (ListViewItem item in files.Items) item.SubItems[1].Text = "В очереди";
            status.Text = "Загрузка модели…"; AppendLog("Начата обработка. Записей: " + files.Items.Count);
            int currentRun = ++runId;
            worker = new Process { StartInfo = new ProcessStartInfo(engine, "--request \"" + requestPath + "\"") {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 }, EnableRaisingEvents = true };
            worker.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                if (!String.IsNullOrEmpty(e.Data)) Dispatch(delegate { if (runId == currentRun) HandleEvent(e.Data); });
            };
            worker.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                if (!String.IsNullOrEmpty(e.Data)) lock (errors) { if (errors.Length < 24000) errors.AppendLine(e.Data); }
            };
            worker.Exited += delegate { Dispatch(delegate { if (runId == currentRun) FinishRun(); }); };
            worker.Start(); worker.BeginOutputReadLine(); worker.BeginErrorReadLine(); UpdateButtons();
        }
        catch (Exception ex)
        {
            running = false; progress.Style = ProgressBarStyle.Blocks;
            AppendLog("Не удалось запустить: " + ex.Message); status.Text = "Ошибка запуска.";
            DeleteRequest(); UpdateButtons();
        }
    }
    private void HandleEvent(string line)
    {
        if (cancelled) return;
        try
        {
            var data = json.Deserialize<Dictionary<string, object>>(line);
            string type = Convert.ToString(data["type"]);
            if (type == "loading") { status.Text = Convert.ToString(data["message"]); return; }
            if (type == "started")
            {
                activeIndex = Convert.ToInt32(data["index"]); files.Items[activeIndex].SubItems[1].Text = "Распознаётся";
                progress.Style = ProgressBarStyle.Blocks;
                status.Text = "Распознаётся: " + Path.GetFileName(Convert.ToString(data["file"]));
                resultFolders.Add(Path.GetDirectoryName(Convert.ToString(data["partial"])));
                AppendLog(status.Text); UpdateButtons();
            }
            else if (type == "progress")
            {
                double seconds = Convert.ToDouble(data["seconds"]), duration = Convert.ToDouble(data["duration"]);
                int index = Convert.ToInt32(data["index"]), total = Convert.ToInt32(data["total"]);
                progress.Value = Math.Max(0, Math.Min(999, (int)(1000 * (index + Math.Min(1, seconds / Math.Max(1, duration))) / total)));
                status.Text = String.Format("Запись {0} из {1}: {2} / {3}", index + 1, total, TimeText(seconds), TimeText(duration));
            }
            else if (type == "result")
            {
                if (activeIndex >= 0) files.Items[activeIndex].SubItems[1].Text = "Готово";
                string path = Convert.ToString(data["txt"]); resultFolders.Add(Path.GetDirectoryName(path));
                AppendLog("Готово: " + path); UpdateButtons();
            }
            else if (type == "file_error")
            {
                string path = Convert.ToString(data["file"]);
                foreach (ListViewItem item in files.Items)
                    if (String.Equals((string)item.Tag, path, StringComparison.OrdinalIgnoreCase)) item.SubItems[1].Text = "Ошибка";
                AppendLog("Ошибка в «" + Path.GetFileName(path) + "»: " + Convert.ToString(data["message"]));
            }
            else if (type == "fatal")
            {
                terminalEvent = true; status.Text = "Обработка не выполнена.";
                AppendLog("Ошибка: " + Convert.ToString(data["message"]));
            }
            else if (type == "done")
            {
                terminalEvent = true; progress.Style = ProgressBarStyle.Blocks; progress.Value = 1000;
                status.Text = "Готово: " + data["completed"] + ". Ошибок: " + data["failed"] + ".";
                AppendLog(status.Text);
            }
        }
        catch (Exception) { AppendLog(line); }
    }
    private static string TimeText(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return String.Format("{0:00}:{1:00}:{2:00}", (int)time.TotalHours, time.Minutes, time.Seconds);
    }
    private void FinishRun()
    {
        // WaitForExit drains asynchronous output before deciding whether a terminal event was received.
        if (worker != null) worker.WaitForExit();
        BeginInvoke((Action)delegate {
            if (!running) return;
            running = false; progress.Style = ProgressBarStyle.Blocks;
            if (cancelled) status.Text = "Остановлено. Уже распознанные фрагменты остаются в файлах .partial.txt.";
            else if (!terminalEvent)
            {
                status.Text = "Программа распознавания завершилась с ошибкой.";
                lock (errors) { AppendLog(errors.Length > 0 ? errors.ToString() : "Не получен итог обработки. Проверьте наличие свободной памяти и файлов программы."); }
            }
            DeleteRequest(); if (worker != null) { worker.Dispose(); worker = null; } UpdateButtons();
        });
    }
    private void CancelRun()
    {
        if (!running || cancelled) return;
        cancelled = true;
        try { if (worker != null && !worker.HasExited) worker.Kill(); } catch (InvalidOperationException) { }
        AppendLog("Обработка остановлена. Файлы .partial.txt содержат только уже распознанную часть.");
    }
    private void DeleteRequest()
    {
        try { if (!String.IsNullOrEmpty(requestPath)) File.Delete(requestPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private void OpenResults()
    {
        foreach (string folder in resultFolders)
            if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
    public void CheckLayout(string imagePath)
    {
        // Render the actual Windows controls for build validation without starting recognition.
        Show(); Application.DoEvents();
        if (start.Enabled || stop.Enabled || !add.Enabled || language.SelectedIndex != 0)
            throw new Exception("Неверное начальное состояние окна.");
        using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(imagePath); }
        Hide();
    }
}
