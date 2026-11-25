using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace OpenCodeChat
{
    internal sealed class FileItem
    {
        public string Path;
        public string Name;
        public long Size;
        public DateTime Modified;
    }

    internal sealed class ModelItem
    {
        public string Id;
        public string Display;
        public bool Mine;
        public bool Offline;
        public string Gguf;

        public ModelItem(string id)
        {
            Id = id;
        }

        public ModelItem(string id, string display, bool mine)
        {
            Id = id;
            Display = display;
            Mine = mine;
        }

        public override string ToString()
        {
            if (Display != null && Display.Length > 0) return Display;
            int slash = Id.IndexOf('/');
            if (slash >= 0 && slash + 1 < Id.Length) return Id.Substring(slash + 1);
            return Id;
        }
    }

    // Одно сообщение переписки. Список показывает его на экране, а экранный
    // диктор читает ToString() целиком — это и есть одно чтение на сообщение.
    internal sealed class MessageItem
    {
        public string Header;
        public string Body;
        public Color HeaderColor;

        public MessageItem(string header, string body, Color headerColor)
        {
            Header = header == null ? "" : header;
            Body = body == null ? "" : body;
            HeaderColor = headerColor;
        }

        public string Plain
        {
            get
            {
                if (Header.Length == 0) return Body;
                if (Body.Length == 0) return Header;
                return Header + "\r\n" + Body;
            }
        }

        public override string ToString()
        {
            return Plain;
        }
    }

    internal sealed class ChatForm : Form
    {
        private readonly string root;
        private readonly string projectsDir;
        private readonly string exePath;
        private readonly string downloadsDir;
        // Подписи авторов в переписке: свои сообщения и ответы агента.
        private readonly string userName = "Я";
        private const string AgentName = "Агент";

        private ListBox transcript;
        private TextBox input;
        private Button detachBtn;
        private Button newChatBtn;
        private Button importBtn;
        private Button logBtn;
        private Button profileBtn;
        private Button serviceBtn;
        private Button balanceBtn;
        private Button addModelBtn;
        private Button updateBtn;
        private bool updating = false;
        private ComboBox modelBox;
        private Label startedLabel;
        private Label attachedLabel;
        private Label statusLabel;
        private ListView filesView;
        private System.Windows.Forms.Timer tick;
        private int elapsed;

        private Font headerFont;
        private Font bodyFont;

        private readonly List<FileItem> pending = new List<FileItem>();
        private string sessionId = null;
        private DateTime startedAt = DateTime.Now;
        private bool busy = false;

        // «Стоп»: пока движок отвечает, здесь лежит его процесс.
        private Button stopBtn;
        private Button copyBtn;
        private Process activeProc;
        private volatile bool stopRequested;
        private string lastAnswer = "";

        // Переписка — список сообщений: каждое сообщение отдельный пункт,
        // экранный диктор читает его сам, когда на нём стоит выбор.
        private readonly List<MessageItem> messages = new List<MessageItem>();
        private System.Windows.Forms.Timer resizeTimer;
        private int measuredWidth = -1;
        private bool remeasuring = false;

        // Своя переписка у каждой модели: файлы лежат в папке chats,
        // имя — из ключа модели. historyText/historyState указывают
        // на переписку выбранной сейчас модели.
        private string historyText;
        private string historyState;
        private readonly string chatsDir;
        private readonly string settingsFile;
        private string preferredModel = "";
        private string currentModelKey = "";
        private bool suppressModelSwitch = false;

        // Проекты: подпапки внутри projects. «Без проекта» — сама папка
        // projects, как было в прежних версиях программы.
        private ComboBox projectBox;
        private Button newProjectBtn;
        private Button deleteProjectBtn;
        private string currentProject = "";
        private bool suppressProjectSwitch = false;
        private const string NoProject = "Без проекта";

        // История, импортированная из чата другой модели. Добавляется
        // в текст следующего запроса один раз, чтобы движок её увидел.
        private string importContext = "";
        private string importSource = "";

        // Офлайн-модели: файлы .gguf и сервер llama.cpp рядом с программой.
        private readonly string modelsFile;
        private readonly string llamaDir;
        private readonly object llamaLock = new object();
        private Process llamaProc;
        private string llamaLoaded = null;
        private string offlineError = "";
        private bool loadingOffline = false;
        private List<string> lastFound = new List<string>();
        private const int OfflinePort = 8099;

        // Версия программы. Сравнивается с номером в теге релиза
        // slonobass-agent-N на GitHub, чтобы кнопка «Обновить программу»
        // знала, есть ли что скачивать.
        internal const int AppVersion = 21;
        private const string ReleasesApi =
            "https://api.github.com/repos/magiyastraha-tech/slonobass-agent/releases?per_page=20";

        private static readonly string[] FallbackModels = new string[]
        {
            "opencode/deepseek-v4.1-flash-free",
            "opencode/nemotron-3.5-super-vl-free",
            "opencode/big-pickle",
            "opencode/hy4-flash-free",
            "opencode/mimo-v2.6-flash-free",
            "opencode/iceman-free",
            "opencode/tidepool-alpha-free",
            "opencode/flint-40b-free",
            "opencode/beam-free",
            "opencode/nemotron-3.5-lightning-free"
        };

        private static readonly string[] ApiModels = new string[]
        {
            "deepseek/deepseek-flash",
            "deepseek/deepseek-v4-pro"
        };

        public ChatForm()
        {
            root = AppDomain.CurrentDomain.BaseDirectory;
            projectsDir = Path.Combine(root, "projects");
            exePath = Path.Combine(root, "engine", "opencode.exe");
            if (!File.Exists(exePath)) exePath = Path.Combine(root, "opencode.exe");
            downloadsDir = FindDownloads();
            string stateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenCodeChat");
            historyText = Path.Combine(stateDir, "chat.txt");
            historyState = Path.Combine(stateDir, "chat.ini");
            chatsDir = Path.Combine(stateDir, "chats");
            settingsFile = Path.Combine(stateDir, "settings.ini");
            modelsFile = Path.Combine(stateDir, "models.txt");
            llamaDir = Path.Combine(root, "engine", "llama");

            if (!Directory.Exists(projectsDir)) Directory.CreateDirectory(projectsDir);
            WriteOfflineConfig();

            headerFont = new Font("Segoe UI", 11f, FontStyle.Bold);
            bodyFont = new Font("Segoe UI", 11f, FontStyle.Regular);

            BuildUi();
            BuildMenus();
            modelBox.SelectedIndexChanged += ModelChanged;
            projectBox.SelectedIndexChanged += ProjectChanged;
            PrepareChatStore();
            RefreshProjects();
            if (currentProject.Length > 0) WriteOfflineConfig();
            if (!LoadHistory()) StartChat(false);

            transcript.Font = bodyFont;
        }

        private static string FindDownloads()
        {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string d = Path.Combine(user, "Downloads");
            try
            {
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
            }
            catch
            {
            }
            if (Directory.Exists(d)) return d;
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return desk;
        }

        private void BuildUi()
        {
            Text = "Slonobass agent " + AppVersion.ToString();
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            ClientSize = new Size(980, 740);
            MinimumSize = new Size(760, 560);
            Font = new Font("Segoe UI", 10f);
            KeyPreview = true;
            KeyDown += FormKeyDown;

            TableLayoutPanel main = new TableLayoutPanel();
            main.Dock = DockStyle.Fill;
            main.ColumnCount = 1;
            main.RowCount = 7;
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 96f));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 190f));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.Padding = new Padding(8);

            // --- header ---
            FlowLayoutPanel header = new FlowLayoutPanel();
            header.AutoSize = true;
            header.WrapContents = true;
            header.Dock = DockStyle.Fill;

            startedLabel = new Label();
            startedLabel.AutoSize = true;
            startedLabel.Padding = new Padding(0, 6, 18, 0);
            startedLabel.AccessibleName = "Начало разговора";
            header.Controls.Add(startedLabel);

            Label modelLabel = new Label();
            modelLabel.Text = "Модель:";
            modelLabel.AutoSize = true;
            modelLabel.Padding = new Padding(0, 8, 4, 0);
            header.Controls.Add(modelLabel);

            modelBox = new ComboBox();
            modelBox.DropDownStyle = ComboBoxStyle.DropDownList;
            modelBox.Width = 300;
            modelBox.Margin = new Padding(0, 3, 12, 3);
            modelBox.AccessibleName = "Модель";
            modelBox.AccessibleDescription = "Список моделей. Сверху раздел «Мой профиль» — модели вашего аккаунта DeepSeek, ниже бесплатные модели. F3 — перейти к списку моделей.";
            header.Controls.Add(modelBox);

            Label projectLabel = new Label();
            projectLabel.Text = "Проект:";
            projectLabel.AutoSize = true;
            projectLabel.Padding = new Padding(0, 8, 4, 0);
            header.Controls.Add(projectLabel);

            projectBox = new ComboBox();
            projectBox.DropDownStyle = ComboBoxStyle.DropDownList;
            projectBox.Width = 220;
            projectBox.Margin = new Padding(0, 3, 6, 3);
            projectBox.AccessibleName = "Проект";
            projectBox.AccessibleDescription = "Проект — отдельная папка внутри папки projects. Агент работает только в папке выбранного проекта. F4 — перейти к списку проектов.";
            header.Controls.Add(projectBox);

            newProjectBtn = new Button();
            newProjectBtn.Text = "Создать &проект…";
            newProjectBtn.AutoSize = true;
            newProjectBtn.Margin = new Padding(0, 3, 6, 3);
            newProjectBtn.AccessibleName = "Создать проект";
            newProjectBtn.AccessibleDescription = "Создаёт папку проекта внутри папки projects";
            newProjectBtn.Click += delegate { CreateProject(); };
            header.Controls.Add(newProjectBtn);

            deleteProjectBtn = new Button();
            deleteProjectBtn.Text = "&Удалить проект";
            deleteProjectBtn.AutoSize = true;
            deleteProjectBtn.Margin = new Padding(0, 3, 12, 3);
            deleteProjectBtn.AccessibleName = "Удалить проект";
            deleteProjectBtn.AccessibleDescription = "Удаляет папку выбранного проекта со всем содержимым";
            deleteProjectBtn.Click += delegate { DeleteProject(); };
            header.Controls.Add(deleteProjectBtn);

            addModelBtn = new Button();
            addModelBtn.Text = "&Добавить модель…";
            addModelBtn.AutoSize = true;
            addModelBtn.Margin = new Padding(0, 3, 12, 3);
            addModelBtn.AccessibleName = "Добавить модель";
            addModelBtn.AccessibleDescription = "Выбрать файл модели .gguf на этом компьютере и добавить его в список";
            addModelBtn.Click += delegate { AddOfflineModel(); };
            header.Controls.Add(addModelBtn);

            profileBtn = new Button();
            profileBtn.Text = "Мой п&рофиль…";
            profileBtn.AutoSize = true;
            profileBtn.AccessibleName = "Мой профиль DeepSeek";
            profileBtn.AccessibleDescription = "Свой аккаунт DeepSeek, оплаченные токены";
            profileBtn.Click += delegate { OpenProfile(); };
            header.Controls.Add(profileBtn);

            serviceBtn = new Button();
            serviceBtn.Text = "Дру&гие сервисы…";
            serviceBtn.AutoSize = true;
            serviceBtn.AccessibleName = "Другие сервисы";
            serviceBtn.AccessibleDescription = "Polza.ai, AnyModel или свой адрес: ключ и список моделей";
            serviceBtn.Click += delegate { OpenOtherService(); };
            header.Controls.Add(serviceBtn);

            balanceBtn = new Button();
            balanceBtn.Text = "Проверить &баланс";
            balanceBtn.AutoSize = true;
            balanceBtn.AccessibleName = "Проверить баланс";
            balanceBtn.AccessibleDescription = "Alt+B: показывает баланс только того сервиса, чья модель выбрана сейчас — Polza.ai или AnyModel";
            balanceBtn.Click += delegate { CheckBalances(balanceBtn); };
            header.Controls.Add(balanceBtn);

            newChatBtn = new Button();
            newChatBtn.Text = "&Новый разговор";
            newChatBtn.AutoSize = true;
            newChatBtn.AccessibleName = "Новый разговор";
            newChatBtn.Click += delegate { NewChat(); };
            header.Controls.Add(newChatBtn);

            logBtn = new Button();
            logBtn.Text = "&Журнал ошибок";
            logBtn.AutoSize = true;
            logBtn.AccessibleName = "Журнал ошибок";
            logBtn.AccessibleDescription = "Папка log с записями об ошибках";
            logBtn.Click += delegate { OpenLogFolder(); };
            header.Controls.Add(logBtn);

            updateBtn = new Button();
            updateBtn.Text = "Обно&вить программу";
            updateBtn.AutoSize = true;
            updateBtn.AccessibleName = "Обновить программу";
            updateBtn.AccessibleDescription = "Проверить новую версию, скачать её и перезапустить программу";
            updateBtn.Click += delegate { CheckForUpdates(); };
            header.Controls.Add(updateBtn);

            main.Controls.Add(header, 0, 0);

            // Пересчёт высот сообщений после изменения ширины окна. С задержкой,
            // чтобы не пересчитывать на каждый пиксель при растягивании.
            // Таймер создаётся до списка: список получает событие Resize уже
            // при добавлении в раскладку, и обработчик должен найти таймер готовым.
            resizeTimer = new System.Windows.Forms.Timer();
            resizeTimer.Interval = 250;
            resizeTimer.Tick += delegate
            {
                resizeTimer.Stop();
                RemeasureMessages();
            };

            // --- transcript ---
            // Список, а не текстовое поле: в списке каждое сообщение — отдельный
            // пункт, стрелки переходят по пунктам, и диктор читает выбранное
            // сообщение целиком сам. Никаких оповещений и выделений, которые
            // перебивают друг друга.
            transcript = new ListBox();
            transcript.Dock = DockStyle.Fill;
            transcript.BorderStyle = BorderStyle.FixedSingle;
            transcript.DrawMode = DrawMode.OwnerDrawVariable;
            transcript.IntegralHeight = false;
            transcript.SelectionMode = SelectionMode.One;
            transcript.BackColor = Color.White;
            transcript.ForeColor = Color.Black;
            transcript.AccessibleName = "Переписка";
            transcript.MeasureItem += TranscriptMeasureItem;
            transcript.DrawItem += TranscriptDrawItem;
            transcript.KeyDown += TranscriptKeyDown;
            transcript.Resize += delegate { QueueRemeasure(); };
            main.Controls.Add(transcript, 0, 1);

            // --- attach state (row 2) ---
            attachedLabel = new Label();
            attachedLabel.AutoSize = true;
            attachedLabel.Text = "Файлы к сообщению: нет";
            attachedLabel.AccessibleName = "Файлы к сообщению";
            attachedLabel.Margin = new Padding(0, 4, 0, 2);
            main.Controls.Add(attachedLabel, 0, 2);

            // --- input editor (row 3) ---
            input = new TextBox();
            input.Dock = DockStyle.Fill;
            input.Multiline = true;
            input.ScrollBars = ScrollBars.Vertical;
            input.AcceptsReturn = true;
            input.Margin = new Padding(0, 0, 0, 4);
            input.AccessibleName = "Сообщение";
            input.KeyDown += InputKeyDown;
            main.Controls.Add(input, 0, 3);

            // --- buttons (row 4): только снятие файлов, остальное — сочетания клавиш ---
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            buttons.Margin = new Padding(0);

            detachBtn = new Button();
            detachBtn.Text = "Снять &файлы";
            detachBtn.Width = 200;
            detachBtn.Height = 28;
            detachBtn.AccessibleName = "Снять прикреплённые файлы";
            detachBtn.Enabled = false;
            detachBtn.Click += delegate { ClearPending(); };
            buttons.Controls.Add(detachBtn);

            stopBtn = new Button();
            stopBtn.Text = "&Стоп";
            stopBtn.Width = 140;
            stopBtn.Height = 28;
            stopBtn.AccessibleName = "Остановить ответ";
            stopBtn.AccessibleDescription = "Обрывает ответ модели; написанное остаётся в переписке";
            stopBtn.Enabled = false;
            stopBtn.Click += delegate { StopAgent(); };
            buttons.Controls.Add(stopBtn);

            copyBtn = new Button();
            copyBtn.Text = "&Копировать сообщение";
            copyBtn.Width = 200;
            copyBtn.Height = 28;
            copyBtn.AccessibleName = "Копировать сообщение";
            copyBtn.AccessibleDescription = "Кладёт выделенное сообщение переписки в буфер обмена";
            copyBtn.Click += delegate { CopyLastAnswer(); };
            buttons.Controls.Add(copyBtn);

            importBtn = new Button();
            importBtn.Text = "&Импорт истории (Alt+I)";
            importBtn.Width = 200;
            importBtn.Height = 28;
            importBtn.AccessibleName = "Импорт истории";
            importBtn.AccessibleDescription = "Alt+I: добавить в этот чат переписку из чата другой модели";
            importBtn.Click += delegate { TriggerImport(); };
            buttons.Controls.Add(importBtn);

            main.Controls.Add(buttons, 0, 4);

            // --- files panel (row 5) ---
            GroupBox filesGroup = new GroupBox();
            filesGroup.Text = "Файлы от агента";
            filesGroup.Dock = DockStyle.Fill;
            filesGroup.Padding = new Padding(6);
            filesGroup.Margin = new Padding(0, 6, 0, 0);

            filesView = new ListView();
            filesView.Dock = DockStyle.Fill;
            filesView.View = View.Details;
            filesView.FullRowSelect = true;
            filesView.MultiSelect = false;
            filesView.HideSelection = false;
            filesView.LabelEdit = false;
            filesView.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            filesView.Columns.Add("Имя", 150);
            filesView.Columns.Add("Время", 60);
            filesView.Columns.Add("Размер", 70);
            filesView.AccessibleName = "Файлы от агента";
            filesView.KeyDown += FilesViewKeyDown;
            filesGroup.Controls.Add(filesView);

            main.Controls.Add(filesGroup, 0, 5);

            // --- status (row 6) ---
            statusLabel = new Label();
            statusLabel.Text = "Готов";
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.AutoSize = false;
            statusLabel.MinimumSize = new Size(0, 26);
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.AccessibleName = "Состояние";
            main.Controls.Add(statusLabel, 0, 6);

            Controls.Add(main);
            KeyPress += FormKeyPress;

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 1000;
            tick.Tick += delegate
            {
                elapsed++;
                if (loadingOffline)
                    statusLabel.Text = "Загружаю офлайн-модель… " + elapsed.ToString() + " с";
                else
                    statusLabel.Text = "Агент отвечает… " + elapsed.ToString() + " с";
            };
        }

        private void BuildMenus()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem save = new ToolStripMenuItem("Сохранить в Загрузки");
            save.Click += delegate { SaveSelected(); };
            menu.Items.Add(save);
            ToolStripMenuItem open = new ToolStripMenuItem("Открыть файл");
            open.Click += delegate { OpenSelected(); };
            menu.Items.Add(open);
            filesView.ContextMenuStrip = menu;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!File.Exists(exePath))
            {
                Log.Write("ERROR", "Не найден движок: " + exePath);
                MessageBox.Show(this,
                    "Не найден движок engine\\opencode.exe.\r\nРаспакуйте архив целиком в одну папку и запускайте Slonobass-Agent.exe из неё.",
                    "Slonobass agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            Log.Write("START", "Движок: " + exePath + "; рабочая папка: " + WorkDir + "; Загрузки: " + downloadsDir
                + "; профиль DeepSeek: " + (Profile.HasKey ? "подключён " + Profile.Hint : "нет")
                + "; другой сервис: " + (Service.HasProfile ? Service.Label + " " + Service.Hint : "нет"));
            if (Profile.HasKey && statusLabel.Text == "Готов") statusLabel.Text = "Готов. Мой профиль DeepSeek подключён: " + Profile.Hint;
            input.Focus();
            LoadModels();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                DialogResult r = MessageBox.Show(this,
                    "Агент ещё отвечает. Закрыть окно?",
                    "Slonobass agent", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) e.Cancel = true;
            }
            if (!e.Cancel)
            {
                SaveHistory();
                StopOffline();
            }
            base.OnFormClosing(e);
        }

        // ---------- chat flow ----------

        private void StartChat(bool keepScreen)
        {
            startedAt = DateTime.Now;
            sessionId = null;
            startedLabel.Text = "Разговор начат: " + startedAt.ToString("dd.MM.yyyy, HH:mm");
            if (!keepScreen)
            {
                messages.Clear();
                transcript.Items.Clear();
            }
            SaveHistory();
        }

        private bool LoadHistory()
        {
            try
            {
                if (!File.Exists(historyState)) return false;
                messages.Clear();
                transcript.Items.Clear();
                if (File.Exists(historyText))
                {
                    LoadMessagesFile(historyText);
                }
                else
                {
                    // Переписка из прежних версий лежала в формате RTF.
                    string legacy = Path.Combine(Path.GetDirectoryName(historyText), "chat.rtf");
                    if (File.Exists(legacy)) LoadLegacyHistory(legacy);
                }
                string[] lines = File.ReadAllLines(historyState);
                string savedDir = null;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.StartsWith("dir=", StringComparison.Ordinal))
                    {
                        savedDir = line.Substring(4).Trim();
                    }
                    else if (line.StartsWith("session=", StringComparison.Ordinal))
                    {
                        string v = line.Substring(8).Trim();
                        if (v.Length > 0) sessionId = v;
                    }
                    else if (line.StartsWith("started=", StringComparison.Ordinal))
                    {
                        DateTime dt;
                        if (DateTime.TryParse(line.Substring(8).Trim(), out dt)) startedAt = dt;
                    }
                    else if (line.StartsWith("model=", StringComparison.Ordinal))
                    {
                        preferredModel = line.Substring(6).Trim();
                    }
                }
                // Если программа переехала в другую папку, движок не найдёт
                // сессию по старому пути — начинаем разговор с новой сессии.
                if (sessionId != null && savedDir != null
                    && !string.Equals(NormalizeDir(savedDir), NormalizeDir(WorkDir), StringComparison.OrdinalIgnoreCase))
                {
                    Log.Write("SESSION", "Сессия из другой папки отброшена: " + savedDir);
                    sessionId = null;
                }
                startedLabel.Text = "Разговор начат: " + startedAt.ToString("dd.MM.yyyy, HH:mm");
                if (messages.Count == 0) return false;
                transcript.SelectedIndex = messages.Count - 1;
                statusLabel.Text = sessionId == null
                    ? "Прошлая переписка восстановлена."
                    : "Прошлый разговор восстановлен, можно продолжать.";
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("ERROR", "Не удалось восстановить переписку: " + ex.Message);
                return false;
            }
        }

        // Формат переписки: каждое сообщение начинается с символа \u0001,
        // дальше строка подписи, дальше текст до следующего \u0001.
        private void SaveHistory()
        {
            try
            {
                string dir = Path.GetDirectoryName(historyText);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                StringBuilder all = new StringBuilder();
                for (int i = 0; i < messages.Count; i++)
                {
                    all.Append('\u0001').Append(messages[i].Header).Append('\n');
                    all.Append(messages[i].Body.Replace("\r\n", "\n")).Append('\n');
                }
                File.WriteAllText(historyText, all.ToString(), new UTF8Encoding(false));
                ModelItem mi = modelBox.SelectedItem as ModelItem;
                string model = currentModelKey;
                if (model.Length == 0) model = mi != null ? KeyOf(mi) : preferredModel;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("session=" + (sessionId == null ? "" : sessionId));
                sb.AppendLine("started=" + startedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("model=" + (model == null ? "" : model));
                sb.AppendLine("dir=" + WorkDir);
                File.WriteAllText(historyState, sb.ToString(), Encoding.UTF8);
                if (model != null && model.Length > 0) preferredModel = model;
            }
            catch (Exception ex)
            {
                Log.Write("ERROR", "Не удалось сохранить переписку: " + ex.Message);
            }
        }

        // ---------- чат у каждой модели ----------

        // Готовим папку chats и переносим туда переписку прежних версий,
        // где чат был один на все модели.
        private void PrepareChatStore()
        {
            try
            {
                if (!Directory.Exists(chatsDir)) Directory.CreateDirectory(chatsDir);
                if (File.Exists(settingsFile))
                {
                    string[] lines = File.ReadAllLines(settingsFile, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        if (line.StartsWith("model=", StringComparison.Ordinal))
                            currentModelKey = line.Substring(6).Trim();
                        else if (line.StartsWith("project=", StringComparison.Ordinal))
                            currentProject = line.Substring(8).Trim();
                    }
                    if (currentProject.Length > 0
                        && !Directory.Exists(Path.Combine(projectsDir, currentProject)))
                    {
                        Log.Write("PROJECT", "Папка прежнего проекта не найдена, открыт «Без проекта»: " + currentProject);
                        currentProject = "";
                    }
                }
                else
                {
                    string legacyModel = "";
                    if (File.Exists(historyState))
                    {
                        string[] lines = File.ReadAllLines(historyState);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            string line = lines[i];
                            if (line.StartsWith("model=", StringComparison.Ordinal))
                                legacyModel = line.Substring(6).Trim();
                        }
                    }
                    currentModelKey = legacyModel;
                    if (currentModelKey.Length > 0 && (File.Exists(historyText) || File.Exists(historyState)))
                    {
                        string name = SafeName(currentModelKey);
                        string text = Path.Combine(chatsDir, name + ".txt");
                        string state = Path.Combine(chatsDir, name + ".ini");
                        if (File.Exists(historyText) && !File.Exists(text)) File.Move(historyText, text);
                        if (File.Exists(historyState) && !File.Exists(state)) File.Move(historyState, state);
                        string legacyRtf = Path.Combine(Path.GetDirectoryName(historyText), "chat.rtf");
                        string movedRtf = Path.Combine(chatsDir, "chat.rtf");
                        if (File.Exists(legacyRtf) && !File.Exists(movedRtf)) File.Move(legacyRtf, movedRtf);
                        Log.Write("CHATS", "Переписка прежней версии перенесена в чат модели: " + currentModelKey);
                    }
                    SaveSettings();
                }
                if (currentModelKey.Length > 0) UseChatPaths(ChatKeyOf(currentModelKey));
            }
            catch (Exception ex)
            {
                Log.Write("ERROR", "Не удалось подготовить папку чатов: " + ex.Message);
            }
        }

        // Рабочая папка движка: папка выбранного проекта или сама projects.
        private string WorkDir
        {
            get
            {
                return currentProject.Length == 0
                    ? projectsDir
                    : Path.Combine(projectsDir, currentProject);
            }
        }

        // Ключ переписки: у каждой модели своя переписка в каждом проекте.
        // Без проекта ключ совпадает с прежним — старые чаты остаются на месте.
        private string ChatKeyOf(string modelKey)
        {
            if (modelKey == null || modelKey.Length == 0) return "";
            return currentProject.Length == 0 ? modelKey : modelKey + "@" + currentProject;
        }

        private string ChatKey
        {
            get { return ChatKeyOf(currentModelKey); }
        }

        // ---------- проекты ----------

        private void RefreshProjects()
        {
            if (projectBox == null) return;
            suppressProjectSwitch = true;
            try
            {
                projectBox.Items.Clear();
                projectBox.Items.Add(NoProject);
                string[] dirs = new string[0];
                try
                {
                    dirs = Directory.GetDirectories(projectsDir);
                }
                catch
                {
                }
                Array.Sort(dirs, StringComparer.CurrentCultureIgnoreCase);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string name = Path.GetFileName(dirs[i]);
                    if (name.Length == 0 || name[0] == '.') continue;
                    projectBox.Items.Add(name);
                }
                if (currentProject.Length > 0)
                {
                    for (int i = 1; i < projectBox.Items.Count; i++)
                    {
                        string item = projectBox.Items[i] as string;
                        if (string.Equals(item, currentProject, StringComparison.OrdinalIgnoreCase))
                        {
                            currentProject = item;
                            break;
                        }
                    }
                }
                SelectProject(currentProject);
            }
            finally
            {
                suppressProjectSwitch = false;
            }
        }

        private void SelectProject(string name)
        {
            if (projectBox == null) return;
            string want = name == null || name.Length == 0 ? NoProject : name;
            for (int i = 0; i < projectBox.Items.Count; i++)
            {
                string item = projectBox.Items[i] as string;
                if (string.Equals(item, want, StringComparison.OrdinalIgnoreCase))
                {
                    projectBox.SelectedIndex = i;
                    return;
                }
            }
            if (projectBox.Items.Count > 0) projectBox.SelectedIndex = 0;
        }

        private void ProjectChanged(object sender, EventArgs e)
        {
            if (suppressProjectSwitch) return;
            string name = projectBox.SelectedItem as string;
            if (name == null) return;
            if (name == NoProject) name = "";
            if (string.Equals(name, currentProject, StringComparison.Ordinal)) return;
            if (busy)
            {
                suppressProjectSwitch = true;
                SelectProject(currentProject);
                suppressProjectSwitch = false;
                statusLabel.Text = "Сейчас идёт ответ. Проект смените, когда агент закончит.";
                return;
            }
            SwitchToProject(name, true);
        }

        // Смена проекта — это и смена рабочей папки, и новый разговор:
        // сессия движка привязана к папке, чужая переписка в проект не тянется.
        private void SwitchToProject(string name, bool announce)
        {
            SaveHistory();
            currentProject = name == null ? "" : name;
            UseChatPaths(ChatKey);
            sessionId = null;
            startedAt = DateTime.Now;
            startedLabel.Text = "Разговор начат: " + startedAt.ToString("dd.MM.yyyy, HH:mm");
            importContext = "";
            importSource = "";
            messages.Clear();
            transcript.Items.Clear();
            if (filesView != null) filesView.Items.Clear();
            WriteOfflineConfig();
            bool restored = LoadHistory();
            if (!restored) StartChat(false);
            SaveSettings();
            Log.Write("PROJECT", "Проект: " + (currentProject.Length == 0 ? "(без проекта)" : currentProject)
                + "; рабочая папка: " + WorkDir);
            if (announce)
            {
                string where = currentProject.Length == 0 ? "Без проекта" : "Проект «" + currentProject + "»";
                statusLabel.Text = restored
                    ? where + ". Сообщений: " + messages.Count.ToString() + ". Рабочая папка: " + WorkDir
                    : where + ". Новый разговор. Рабочая папка: " + WorkDir;
            }
        }

        private void CreateProject()
        {
            string raw;
            using (PromptForm dlg = new PromptForm("Новый проект",
                "В папке projects появится папка с таким названием. Агент будет работать только в ней.",
                "Название проекта"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) { input.Focus(); return; }
                raw = dlg.Value;
            }
            string name = MakeProjectName(raw);
            if (name.Length == 0)
            {
                MessageBox.Show(this,
                    "Название проекта пустое или содержит недопустимые символы.\r\n"
                    + "Нельзя использовать: \\ / : * ? \" < > |",
                    "Новый проект", MessageBoxButtons.OK, MessageBoxIcon.Information);
                input.Focus();
                return;
            }
            string path = Path.Combine(projectsDir, name);
            bool existed = Directory.Exists(path);
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось создать папку проекта: " + ex.Message,
                    "Новый проект", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            // На диске регистр может отличаться от введённого — берём имя
            // папки как оно есть, иначе одна и та же папка даст два разных чата.
            try
            {
                string[] dirs = Directory.GetDirectories(projectsDir);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string n = Path.GetFileName(dirs[i]);
                    if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { name = n; break; }
                }
            }
            catch
            {
            }
            Log.Write("PROJECT", (existed ? "Открыт существующий проект: " : "Создан проект: ") + name);
            RefreshProjects();
            suppressProjectSwitch = true;
            SwitchToProject(name, false);
            SelectProject(name);
            suppressProjectSwitch = false;
            statusLabel.Text = (existed
                    ? "Проект «" + name + "» уже был — открыт. "
                    : "Проект «" + name + "» создан. ")
                + "Рабочая папка: " + WorkDir;
            input.Focus();
        }

        private void DeleteProject()
        {
            if (currentProject.Length == 0)
            {
                statusLabel.Text = "Сначала выберите проект в списке.";
                MessageBox.Show(this,
                    "Выберите проект в списке. «Без проекта» — это сама папка projects, её удалить нельзя.",
                    "Удалить проект", MessageBoxButtons.OK, MessageBoxIcon.Information);
                projectBox.Focus();
                return;
            }
            string name = currentProject;
            string path = Path.Combine(projectsDir, name);
            int files = 0;
            try
            {
                files = Directory.GetFiles(path, "*", SearchOption.AllDirectories).Length;
            }
            catch
            {
            }
            DialogResult r = MessageBox.Show(this,
                "Удалить проект «" + name + "» со всем содержимым?\r\n\r\n"
                + "Папка: " + path + "\r\nФайлов внутри: " + files.ToString() + ".\r\n\r\n"
                + "Это действие нельзя отменить.",
                "Удалить проект", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) { input.Focus(); return; }

            // Сначала выходим из проекта: движок не должен работать в папке,
            // которую мы собираемся удалить.
            suppressProjectSwitch = true;
            SwitchToProject("", false);
            suppressProjectSwitch = false;
            try
            {
                Directory.Delete(path, true);
                Log.Write("PROJECT", "Удалён проект: " + name + "; файлов: " + files.ToString());
                statusLabel.Text = "Проект «" + name + "» удалён. Открыт «Без проекта».";
            }
            catch (Exception ex)
            {
                Log.Exception("Удаление проекта", ex);
                statusLabel.Text = "Не удалось удалить проект: " + ex.Message;
                MessageBox.Show(this, "Не удалось удалить папку проекта: " + ex.Message,
                    "Удалить проект", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshProjects();
            input.Focus();
        }

        private static string MakeProjectName(string raw)
        {
            if (raw == null) return "";
            string name = raw.Trim();
            if (name.Length == 0) return "";
            if (name == "." || name == "..") return "";
            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < name.Length; i++)
            {
                for (int j = 0; j < bad.Length; j++)
                {
                    if (name[i] == bad[j]) return "";
                }
            }
            if (name.Length > 80) name = name.Substring(0, 80).Trim();
            return name;
        }

        // Баланс только того сервиса, чьей моделью работаем сейчас: у каждой
        // модели в списке видно, откуда она. Модели other/ приходят из
        // подключённого сервиса, остальные (бесплатные, свой DeepSeek,
        // офлайн) баланс Polza.ai и AnyModel не тратят.
        private void CheckBalances(Button btn)
        {
            if (busy)
            {
                statusLabel.Text = "Сейчас идёт ответ. Баланс проверю, когда агент закончит.";
                return;
            }

            ModelItem mi = modelBox.SelectedItem as ModelItem;
            string provider = "";
            if (mi != null && mi.Id != null
                && mi.Id.StartsWith("other/", StringComparison.OrdinalIgnoreCase)
                && Service.HasProfile)
            {
                provider = Service.ProviderOf(Service.BaseUrl);
            }

            if (provider.Length == 0)
            {
                string text;
                if (mi == null)
                {
                    text = "Модель не выбрана. Баланс показывается только у сервиса, чья модель выбрана: выберите модель Polza.ai или AnyModel.";
                }
                else if (mi.Offline)
                {
                    text = "Выбрана офлайн-модель — она работает на этом компьютере и баланс сервисов не тратит. Чтобы увидеть баланс, выберите модель Polza.ai или AnyModel.";
                }
                else if (mi.Id != null && mi.Id.StartsWith("deepseek/", StringComparison.OrdinalIgnoreCase))
                {
                    text = "Выбрана модель вашего профиля DeepSeek — запросы тратят токены аккаунта DeepSeek, а не Polza.ai или AnyModel. Баланс DeepSeek показан на его сайте, в разделе ключей.";
                }
                else if (mi.Id != null && mi.Id.StartsWith("other/", StringComparison.OrdinalIgnoreCase))
                {
                    text = "Модель подключена через сервис «" + Service.Label + "» с адресом " + Service.BaseUrl
                        + " — баланс этого адреса программа не знает. Баланс можно проверить у Polza.ai и AnyModel.";
                }
                else
                {
                    text = "Выбрана бесплатная модель — она баланс сервисов не тратит. Чтобы увидеть баланс, выберите модель Polza.ai или AnyModel.";
                }
                statusLabel.Text = text;
                MessageBox.Show(this, text, "Проверка баланса",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                input.Focus();
                return;
            }

            string providerName = provider == "polza" ? "Polza.ai" : "AnyModel";
            string key = Service.KeyFor(provider);
            if (key.Length == 0)
            {
                string missing = "Ключ " + providerName + " не сохранён."
                    + "\r\n\r\nОткройте «Другие сервисы…», выберите " + providerName
                    + ", вставьте ключ и сохраните — ключ запомнится, и баланс можно будет проверить в любой момент.";
                statusLabel.Text = "Ключ " + providerName + " не сохранён.";
                MessageBox.Show(this, missing, "Проверка баланса",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                input.Focus();
                return;
            }

            btn.Enabled = false;
            statusLabel.Text = "Проверяю баланс: " + providerName + "…";
            Log.Write("BALANCE", "Запрос баланса: " + providerName + " (модель " + mi.Id + ").");
            Thread t = new Thread(delegate()
            {
                string result = provider == "polza" ? BalanceInfo.Polza(key) : BalanceInfo.Anymodel(key);
                try
                {
                    BeginInvoke((MethodInvoker)delegate { ShowBalances(btn, result); });
                }
                catch
                {
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void ShowBalances(Button btn, string result)
        {
            if (btn != null) btn.Enabled = true;
            statusLabel.Text = result;
            Log.Write("BALANCE", result);
            MessageBox.Show(this, result, "Баланс сервиса",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            input.Focus();
        }

        private static string SafeName(string key)
        {
            if (key == null) key = "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < key.Length && sb.Length < 60; i++)
            {
                char c = key[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            if (sb.Length == 0) sb.Append("model");
            uint h = 2166136261u;
            for (int i = 0; i < key.Length; i++)
            {
                h ^= key[i];
                h *= 16777619u;
            }
            return sb.ToString() + "-" + h.ToString("x8");
        }

        private void UseChatPaths(string key)
        {
            string name = SafeName(key);
            historyText = Path.Combine(chatsDir, name + ".txt");
            historyState = Path.Combine(chatsDir, name + ".ini");
        }

        private void SaveSettings()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("model=" + (currentModelKey == null ? "" : currentModelKey));
                sb.AppendLine("project=" + currentProject);
                File.WriteAllText(settingsFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Write("ERROR", "Не удалось сохранить настройки: " + ex.Message);
            }
        }

        // Переписка прежних версий принадлежит модели, которую пользователь
        // выберет первой: переносим её файлы в чат этой модели.
        private void AdoptLegacy(string key)
        {
            try
            {
                if (key == null || key.Length == 0) return;
                string name = SafeName(key);
                string text = Path.Combine(chatsDir, name + ".txt");
                string state = Path.Combine(chatsDir, name + ".ini");
                if (File.Exists(historyText) && !File.Exists(text)) File.Move(historyText, text);
                if (File.Exists(historyState) && !File.Exists(state)) File.Move(historyState, state);
                string legacyRtf = Path.Combine(Path.GetDirectoryName(historyText), "chat.rtf");
                string movedRtf = Path.Combine(chatsDir, "chat.rtf");
                if (File.Exists(legacyRtf) && !File.Exists(movedRtf)) File.Move(legacyRtf, movedRtf);
            }
            catch (Exception ex)
            {
                Log.Write("ERROR", "Не удалось перенести старую переписку: " + ex.Message);
            }
        }

        private void ModelChanged(object sender, EventArgs e)
        {
            if (suppressModelSwitch) return;
            ModelItem mi = modelBox.SelectedItem as ModelItem;
            if (mi == null) return;
            string key = KeyOf(mi);
            if (string.Equals(key, currentModelKey, StringComparison.Ordinal)) return;
            if (busy)
            {
                suppressModelSwitch = true;
                SelectByKey(currentModelKey);
                suppressModelSwitch = false;
                statusLabel.Text = "Сейчас идёт ответ. Модель смените, когда агент закончит.";
                return;
            }
            SwitchToModel(mi, true);
        }

        private bool SelectByKey(string key)
        {
            if (key == null || key.Length == 0) return false;
            for (int i = 0; i < modelBox.Items.Count; i++)
            {
                ModelItem m = modelBox.Items[i] as ModelItem;
                if (m != null && string.Equals(KeyOf(m), key, StringComparison.Ordinal))
                {
                    modelBox.SelectedIndex = i;
                    return true;
                }
            }
            return false;
        }

        private void SwitchToModel(ModelItem item, bool announce)
        {
            string key = KeyOf(item);
            SaveHistory();
            if (currentModelKey.Length == 0) AdoptLegacy(key);
            currentModelKey = key;
            preferredModel = key;
            UseChatPaths(ChatKeyOf(key));
            sessionId = null;
            startedAt = DateTime.Now;
            startedLabel.Text = "Разговор начат: " + startedAt.ToString("dd.MM.yyyy, HH:mm");
            importContext = "";
            importSource = "";
            messages.Clear();
            transcript.Items.Clear();
            bool restored = LoadHistory();
            if (!restored) StartChat(false);
            SaveSettings();
            if (announce)
            {
                string label = item != null ? item.ToString() : key;
                statusLabel.Text = restored
                    ? "Чат модели «" + label + "». Сообщений: " + messages.Count.ToString() + "."
                    : "Новый чат модели «" + label + "».";
            }
        }

        private void AlignChatToSelection(bool announce)
        {
            ModelItem mi = modelBox.SelectedItem as ModelItem;
            if (mi == null) return;
            string key = KeyOf(mi);
            if (string.Equals(key, currentModelKey, StringComparison.Ordinal)) return;
            SwitchToModel(mi, announce);
        }

        private List<MessageItem> ReadMessages(string path)
        {
            List<MessageItem> res = new List<MessageItem>();
            if (path == null || !File.Exists(path)) return res;
            string all = File.ReadAllText(path, Encoding.UTF8);
            string[] parts = all.Split('\u0001');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0) continue;
                int nl = part.IndexOf('\n');
                string header, body;
                if (nl < 0)
                {
                    header = part.TrimEnd('\r');
                    body = "";
                }
                else
                {
                    header = part.Substring(0, nl).TrimEnd('\r');
                    body = part.Substring(nl + 1).TrimEnd('\r', '\n');
                }
                if (header.Length == 0 && body.Length == 0) continue;
                header = NormalizeHeader(header);
                body = body.Replace("\r\n", "\n").Replace("\n", "\r\n");
                res.Add(new MessageItem(header, body, HeaderColorFor(header)));
            }
            return res;
        }

        private static int CountMessages(string path)
        {
            try
            {
                if (!File.Exists(path)) return 0;
                string all = File.ReadAllText(path, Encoding.UTF8);
                string[] parts = all.Split('\u0001');
                int n = 0;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Trim().Length > 0) n++;
                }
                return n;
            }
            catch
            {
                return 0;
            }
        }

        // Alt+I: выбрать чат другой модели и добавить его сообщения в текущий.
        private void ImportHistory()
        {
            List<ModelItem> sources = new List<ModelItem>();
            List<int> counts = new List<int>();
            List<string> paths = new List<string>();
            for (int i = 0; i < modelBox.Items.Count; i++)
            {
                ModelItem m = modelBox.Items[i] as ModelItem;
                if (m == null) continue;
                string key = KeyOf(m);
                if (key.Length == 0 || string.Equals(key, currentModelKey, StringComparison.Ordinal)) continue;
                string text = Path.Combine(chatsDir, SafeName(ChatKeyOf(key)) + ".txt");
                bool dup = false;
                for (int j = 0; j < paths.Count; j++)
                {
                    if (string.Equals(paths[j], text, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                }
                if (dup) continue;
                sources.Add(m);
                counts.Add(CountMessages(text));
                paths.Add(text);
            }
            int withHistory = 0;
            for (int i = 0; i < counts.Count; i++) if (counts[i] > 0) withHistory++;
            Log.Write("IMPORT", "Других моделей в списке: " + sources.Count.ToString()
                + "; из них с историей: " + withHistory.ToString()
                + "; текущая модель: " + (currentModelKey.Length > 0 ? currentModelKey : "(нет)")
                + "; проект: " + (currentProject.Length == 0 ? "(без проекта)" : currentProject) + ".");
            if (sources.Count == 0)
            {
                statusLabel.Text = "Других моделей в списке пока нет.";
                MessageBox.Show(this,
                    "Импортировать нечего: пока в списке только одна модель.\r\n"
                    + "Переписка каждой модели — отдельный чат, и её можно добавить в текущий.",
                    "Импорт истории", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int chosen;
            using (ImportForm dlg = new ImportForm(sources, counts))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) { input.Focus(); return; }
                chosen = dlg.SelectedIndex;
            }
            if (chosen < 0 || chosen >= sources.Count) { input.Focus(); return; }

            ModelItem src = sources[chosen];
            List<MessageItem> list = ReadMessages(paths[chosen]);
            string label = src.ToString();
            if (list.Count == 0)
            {
                statusLabel.Text = "У чата «" + label + "» нет сообщений.";
                MessageBox.Show(this,
                    "У чата «" + label + "» пока нет сообщений — импортировать нечего.\r\n"
                    + "Поговорите с этой моделью, и её переписку можно будет добавить сюда.",
                    "Импорт истории", MessageBoxButtons.OK, MessageBoxIcon.Information);
                input.Focus();
                return;
            }

            int firstAdded = messages.Count;
            AddMessage("Импорт истории из чата «" + label + "»",
                "Сообщений: " + list.Count.ToString() + ".", Color.FromArgb(0x1F, 0x3A, 0x5F));
            for (int i = 0; i < list.Count; i++)
            {
                AddMessage(list[i].Header, list[i].Body);
            }

            // Движок не знает о сообщениях другого чата: передаём их
            // в следующем запросе. Берём последние сообщения, чтобы
            // не упереться в ограничение длины командной строки.
            const int limit = 8000;
            StringBuilder tail = new StringBuilder();
            int used = 0;
            bool trimmed = false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                StringBuilder one = new StringBuilder();
                if (list[i].Header.Length > 0) one.Append(list[i].Header).Append("\r\n");
                one.Append(list[i].Body).Append("\r\n\r\n");
                if (used + one.Length > limit && used > 0) { trimmed = true; break; }
                tail.Insert(0, one.ToString());
                used += one.Length;
            }
            StringBuilder ctx = new StringBuilder();
            ctx.Append("Ниже — история, импортированная из чата другой модели («");
            ctx.Append(label).Append("»). Используй её как продолжение этого разговора.\r\n\r\n");
            if (trimmed) ctx.Append("(Самое начало истории не поместилось и опущено.)\r\n\r\n");
            ctx.Append(tail.ToString());
            importContext = ctx.ToString();
            importSource = label;

            SaveHistory();
            if (firstAdded >= 0 && firstAdded < messages.Count) transcript.SelectedIndex = firstAdded;
            statusLabel.Text = "Импортировано сообщений: " + list.Count.ToString()
                + " из чата «" + label + "». Агент учтёт их в следующем сообщении.";
            Log.Write("IMPORT", "Импорт из чата «" + label + "»: сообщений " + list.Count.ToString()
                + "; в запрос пойдёт " + importContext.Length.ToString() + " символов.");
            input.Focus();
        }

        private static string NormalizeDir(string dir)
        {
            if (dir == null) return "";
            return dir.TrimEnd('\\', '/');
        }

        private void LoadMessagesFile(string path)
        {
            List<MessageItem> list = ReadMessages(path);
            for (int i = 0; i < list.Count; i++) AddMessage(list[i].Header, list[i].Body);
        }

        private void LoadLegacyHistory(string path)
        {
            using (RichTextBox box = new RichTextBox())
            {
                box.LoadFile(path, RichTextBoxStreamType.RichText);
                string t = box.Text;
                List<int> starts = new List<int>();
                List<string> headers = new List<string>();
                int pos = 0;
                while (pos <= t.Length)
                {
                    int end = t.IndexOf('\n', pos);
                    if (end < 0) end = t.Length;
                    int lineEnd = end > pos && t[end - 1] == '\r' ? end - 1 : end;
                    string line = t.Substring(pos, lineEnd - pos);
                    if (!line.StartsWith("Разговор начат", StringComparison.Ordinal)
                        && Regex.IsMatch(line, @"^(.+?), (\d{1,2}:\d{2})$"))
                    {
                        starts.Add(pos);
                        headers.Add(line);
                    }
                    if (end >= t.Length) break;
                    pos = end + 1;
                }
                for (int i = 0; i < starts.Count; i++)
                {
                    int lineEnd = t.IndexOf('\n', starts[i]);
                    lineEnd = lineEnd < 0 ? t.Length : lineEnd + 1;
                    int to = i + 1 < starts.Count ? starts[i + 1] : t.Length;
                    string body = t.Substring(lineEnd, Math.Max(0, to - lineEnd)).Trim('\r', '\n', ' ');
                    AddMessage(NormalizeHeader(headers[i]), body);
                }
            }
        }

        private void NewChat()
        {
            if (busy)
            {
                statusLabel.Text = "Сейчас идёт ответ. Подождите.";
                return;
            }
            DialogResult r = MessageBox.Show(this,
                "Начать новый разговор? Переписка этой модели в текущем проекте очистится, "
                + "у других моделей и проектов свои чаты.",
                "Новый разговор", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            StartChat(false);
            statusLabel.Text = "Новый разговор";
            input.Focus();
        }

        // Старые записи переписки хранят прежние подписи: «Лия» и
        // «Slonobass agent · модель». Показываем их как «Я» и «Агент»,
        // чтобы история выглядела одинаково с новыми сообщениями.
        private static string NormalizeHeader(string header)
        {
            if (header == null || header.Length == 0) return header;
            if (header.StartsWith("Лия", StringComparison.Ordinal)
                && (header.Length == 3 || header[3] == ',' || header[3] == ' '))
                return "Я" + header.Substring(3);
            if (header.StartsWith("Slonobass agent", StringComparison.Ordinal))
            {
                int comma = header.LastIndexOf(',');
                return comma > 0 ? AgentName + header.Substring(comma) : AgentName;
            }
            return header;
        }

        private static Color HeaderColorFor(string header)
        {
            if (header.StartsWith(AgentName, StringComparison.Ordinal))
                return Color.FromArgb(0x1F, 0x55, 0x9E);
            if (header.StartsWith("Файлы от агента", StringComparison.Ordinal))
                return Color.FromArgb(0x7A, 0x4A, 0x00);
            if (header.Length == 0)
                return Color.FromArgb(0x60, 0x60, 0x60);
            return Color.FromArgb(0x1A, 0x5F, 0x1A);
        }

        private void AddMessage(string header, string body)
        {
            AddMessage(header, body, HeaderColorFor(header));
        }

        private void AddMessage(string header, string body, Color headerColor)
        {
            MessageItem m = new MessageItem(header, body, headerColor);
            messages.Add(m);
            transcript.Items.Add(m);
        }

        private void AppendBlock(string header, string body, Color headerColor)
        {
            MessageItem m = new MessageItem(header, body, headerColor);
            messages.Add(m);
            transcript.Items.Add(m);
            // Выделение переходит к новому сообщению и держит список внизу.
            // Фокус не забираем: если человек печатает, он остаётся в поле ввода.
            transcript.SelectedIndex = transcript.Items.Count - 1;
            SaveHistory();
        }

        private void InputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            if (e.Control)
            {
                // Ctrl+Enter — перенос строки, как в обычных чат-программах.
                e.SuppressKeyPress = true;
                e.Handled = true;
                int pos = input.SelectionStart;
                input.SelectedText = Environment.NewLine;
                input.SelectionStart = pos + Environment.NewLine.Length;
                return;
            }
            // Простой Enter и Shift+Enter отправляют сообщение.
            e.SuppressKeyPress = true;
            e.Handled = true;
            Send();
        }

        // Набор текста в любом месте программы уводит фокус в редактор.
        private void FormKeyPress(object sender, KeyPressEventArgs e)
        {
            char c = e.KeyChar;
            Control a = ActiveControl;
            if (a == input) return;
            // В редакторе переписки текст не набирается — уводим его в поле ввода.
            if (a != null && a is TextBox && !((TextBox)a).ReadOnly) return;
            if (modelBox != null && a == modelBox && modelBox.DroppedDown) return;
            if (c == '\b')
            {
                input.Focus();
                if (input.SelectionLength > 0) input.SelectedText = "";
                else if (input.SelectionStart > 0)
                {
                    int pos = input.SelectionStart;
                    input.Text = input.Text.Remove(pos - 1, 1);
                    input.SelectionStart = pos - 1;
                }
                e.Handled = true;
                return;
            }
            if (c == ' ' && a is ButtonBase) return;
            if (c >= ' ' && c != '\x7f')
            {
                input.Focus();
                input.SelectedText = c.ToString();
                e.Handled = true;
            }
        }

        // ---------- переписка: отрисовка и переходы ----------

        private const TextFormatFlags WrapFlags =
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

        private int TranscriptTextWidth()
        {
            // Ширину всегда оставляем без полосы прокрутки: иначе длинное
            // сообщение уходило бы под неё и обрезалось справа.
            int w = transcript.ClientSize.Width - 16 - SystemInformation.VerticalScrollBarWidth;
            if (w < 80) w = 80;
            return w;
        }

        private static int MeasureWrapped(string text, Font font, int width)
        {
            if (text == null || text.Length == 0) return 0;
            return TextRenderer.MeasureText(text, font, new Size(width, 1000000), WrapFlags).Height;
        }

        private void TranscriptMeasureItem(object sender, MeasureItemEventArgs e)
        {
            MessageItem m = e.Index >= 0 && e.Index < messages.Count ? messages[e.Index] : null;
            if (m == null)
            {
                e.ItemHeight = 24;
                return;
            }
            int width = TranscriptTextWidth();
            int h = 10;
            if (m.Header.Length > 0) h += MeasureWrapped(m.Header, headerFont, width) + 2;
            h += MeasureWrapped(m.Body, bodyFont, width);
            e.ItemHeight = Math.Max(24, h);
        }

        private void TranscriptDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= messages.Count) return;
            MessageItem m = messages[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (SolidBrush back = new SolidBrush(selected ? SystemColors.Highlight : Color.White))
            {
                e.Graphics.FillRectangle(back, e.Bounds);
            }
            System.Drawing.Drawing2D.GraphicsState saved = e.Graphics.Save();
            e.Graphics.SetClip(e.Bounds);
            int width = TranscriptTextWidth();
            int y = e.Bounds.Top + 4;
            if (m.Header.Length > 0)
            {
                int hh = MeasureWrapped(m.Header, headerFont, width);
                Rectangle hr = new Rectangle(e.Bounds.Left + 7, y, width, hh);
                TextRenderer.DrawText(e.Graphics, m.Header, headerFont, hr,
                    selected ? SystemColors.HighlightText : m.HeaderColor, WrapFlags);
                y = hr.Bottom + 2;
            }
            if (m.Body.Length > 0)
            {
                Rectangle br = new Rectangle(e.Bounds.Left + 7, y, width,
                    Math.Max(0, e.Bounds.Bottom - y - 4));
                TextRenderer.DrawText(e.Graphics, m.Body, bodyFont, br,
                    selected ? SystemColors.HighlightText : Color.Black, WrapFlags);
            }
            e.Graphics.Restore(saved);
        }

        private void QueueRemeasure()
        {
            // Список может прислать Resize ещё до того, как окно собрано
            // до конца; тогда ни списка, ни таймера может не быть.
            if (transcript == null || resizeTimer == null) return;
            // Пересчёт сам может изменить ширину клиентской области (появление
            // или исчезновение полосы прокрутки), и список пришлёт Resize.
            // Без этого флага получается вечный цикл: пересчёт — Resize —
            // пересчёт, а экранный диктор заново читает выбранное сообщение.
            if (remeasuring) return;
            if (transcript.ClientSize.Width == measuredWidth) return;
            resizeTimer.Stop();
            resizeTimer.Start();
        }

        private void RemeasureMessages()
        {
            if (remeasuring) return;
            // Ширина запоминается после пересчёта, а не до: переключение
            // режима отрисовки меняет полосу прокрутки и вместе с ней ширину.
            // Если запомнить старую, сравнение в QueueRemeasure никогда не
            // совпадёт и таймер будет запускаться снова и снова.
            if (transcript.Items.Count == 0)
            {
                measuredWidth = transcript.ClientSize.Width;
                return;
            }
            int top = transcript.TopIndex;
            int sel = transcript.SelectedIndex;
            remeasuring = true;
            try
            {
                transcript.BeginUpdate();
                // Переключение режима заставляет список заново измерить пункты
                // под новую ширину окна.
                transcript.DrawMode = DrawMode.OwnerDrawFixed;
                transcript.DrawMode = DrawMode.OwnerDrawVariable;
                transcript.EndUpdate();
                // Выделение и прокрутку возвращаем, только если список их
                // действительно сдвинул: лишние присваивания — это лишние
                // события для экранного диктора.
                if (sel >= 0 && sel < transcript.Items.Count && transcript.SelectedIndex != sel)
                    transcript.SelectedIndex = sel;
                if (top >= 0 && top < transcript.Items.Count && transcript.TopIndex != top)
                    transcript.TopIndex = top;
            }
            finally
            {
                remeasuring = false;
                measuredWidth = transcript.ClientSize.Width;
            }
        }

        private void SelectMessage(int index)
        {
            if (index < 0 || index >= transcript.Items.Count) return;
            transcript.SelectedIndex = index;
            transcript.Focus();
        }

        // Собеседник сообщения. Модель в подписи меняется, поэтому всё,
        // что от «Slonobass agent», считается одним собеседником.
        private string SpeakerAt(int index)
        {
            for (int i = index; i >= 0; i--)
            {
                string h = messages[i].Header;
                if (h.Length == 0) continue;
                int comma = h.LastIndexOf(", ", StringComparison.Ordinal);
                if (comma > 0) h = h.Substring(0, comma);
                int dot = h.IndexOf(" · ", StringComparison.Ordinal);
                if (dot > 0) h = h.Substring(0, dot);
                return h.Trim();
            }
            return "";
        }

        private void MoveBetweenSpeakers(int dir)
        {
            if (transcript.Items.Count == 0) return;
            int cur = transcript.SelectedIndex;
            if (cur < 0) cur = transcript.Items.Count - 1;
            string mine = SpeakerAt(cur);
            if (dir > 0)
            {
                for (int i = cur + 1; i < messages.Count; i++)
                {
                    if (!string.Equals(SpeakerAt(i), mine, StringComparison.Ordinal))
                    {
                        SelectMessage(i);
                        return;
                    }
                }
            }
            else
            {
                int i = cur;
                while (i > 0 && string.Equals(SpeakerAt(i - 1), mine, StringComparison.Ordinal)) i--;
                if (i == cur)
                {
                    if (i == 0) return;
                    i--;
                    string prev = SpeakerAt(i);
                    while (i > 0 && string.Equals(SpeakerAt(i - 1), prev, StringComparison.Ordinal)) i--;
                }
                SelectMessage(i);
            }
        }

        private static bool IsWebLink(string url)
        {
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private void OpenLink(string url)
        {
            if (url == null) return;
            url = url.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '»', '"', '\'');
            if (!IsWebLink(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                statusLabel.Text = "Открываю ссылку: " + url;
            }
            catch (Exception ex)
            {
                Log.Exception("Открыть ссылку", ex);
                statusLabel.Text = "Не удалось открыть ссылку: " + ex.Message;
            }
        }

        private int CurrentMessageIndex()
        {
            int idx = transcript.SelectedIndex;
            if (idx < 0 && messages.Count > 0) idx = messages.Count - 1;
            return idx;
        }

        private void OpenLinkAtSelection()
        {
            int idx = CurrentMessageIndex();
            if (idx < 0 || idx >= messages.Count)
            {
                statusLabel.Text = "В переписке нет ссылки.";
                return;
            }
            Match m = Regex.Match(messages[idx].Plain, @"https?://[^\s<>""']+");
            if (m.Success) OpenLink(m.Value);
            else statusLabel.Text = "В этом сообщении нет ссылки.";
        }

        private void CopySelectedMessage()
        {
            int idx = CurrentMessageIndex();
            if (idx < 0 || idx >= messages.Count)
            {
                statusLabel.Text = "В переписке пока нет сообщений.";
                return;
            }
            try
            {
                Clipboard.SetText(messages[idx].Plain);
                statusLabel.Text = "Сообщение скопировано.";
            }
            catch (Exception ex)
            {
                Log.Exception("Копирование сообщения", ex);
                statusLabel.Text = "Не удалось скопировать: " + ex.Message;
            }
        }

        // ---------- hotkeys ----------

        // Сочетания с Alt ловим на уровне формы, раньше поля ввода и кнопок:
        // так они не зависят от того, где стоит фокус, и не уходят в мнемоники.
        // Ctrl+I оставлен запасным: в части раскладок и под программами
        // экранного доступа он доходит как Ctrl+Tab (оба дают табуляцию).
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            bool alt = (keyData & Keys.Alt) == Keys.Alt;
            bool shift = (keyData & Keys.Shift) == Keys.Shift;
            Keys code = keyData & Keys.KeyCode;
            if (alt && !ctrl && !shift && code == Keys.I)
            {
                ImportHotkey("Alt+I");
                return true;
            }
            if (alt && !ctrl && !shift && code == Keys.A)
            {
                AttachHotkey();
                return true;
            }
            if (alt && !ctrl && !shift && code == Keys.B)
            {
                CheckBalances(balanceBtn);
                return true;
            }
            if (ctrl && !alt && !shift && (code == Keys.I || code == Keys.Tab))
            {
                ImportHotkey("Ctrl+I");
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void TriggerImport()
        {
            Log.Write("HOTKEY", "Нажата кнопка «Импорт истории».");
            StartImport();
        }

        private void ImportHotkey(string combo)
        {
            Log.Write("HOTKEY", "Нажато " + combo + ": импорт истории.");
            StartImport();
        }

        private void StartImport()
        {
            if (busy)
            {
                statusLabel.Text = "Сейчас идёт ответ. Импорт — после ответа.";
                return;
            }
            ImportHistory();
        }

        private void AttachHotkey()
        {
            Log.Write("HOTKEY", "Нажато Alt+A: прикрепить файл.");
            if (busy)
            {
                statusLabel.Text = "Сейчас идёт ответ. Дождитесь его, потом прикрепите файл.";
                return;
            }
            AttachFiles();
        }

        private void FormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && busy)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                StopAgent();
            }
            else if (e.KeyCode == Keys.F1)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                FocusLastMessage();
            }
            else if (e.KeyCode == Keys.F2)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                FocusFiles();
            }
            else if (e.KeyCode == Keys.F3)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                modelBox.Focus();
                statusLabel.Text = "Список моделей. Стрелками выберите модель.";
            }
            else if (e.KeyCode == Keys.F4)
            {
                // Если фокус уже в списке проектов, F4 отдаём списку —
                // он сам раскрывает перечень проектов.
                if (projectBox != null && projectBox.Focused) return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                FocusProjects();
            }
            else if (e.Control && e.KeyCode == Keys.E)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                input.Focus();
            }
            else if (e.Control && e.KeyCode == Keys.N)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                NewChat();
            }
        }

        private void FocusProjects()
        {
            if (projectBox == null) return;
            projectBox.Focus();
            string cur = projectBox.SelectedItem == null
                ? "Без проекта"
                : projectBox.SelectedItem.ToString();
            statusLabel.Text = "Проект: " + cur + ". Стрелками выберите проект, F4 — открыть список.";
        }

        private void FocusLastMessage()
        {
            if (transcript.Items.Count == 0)
            {
                transcript.Focus();
                statusLabel.Text = "Переписка пока пуста.";
                return;
            }
            SelectMessage(transcript.Items.Count - 1);
        }

        private void FocusFiles()
        {
            filesView.Focus();
            if (filesView.Items.Count > 0)
            {
                int last = filesView.Items.Count - 1;
                filesView.Items[last].Selected = true;
                filesView.Items[last].Focused = true;
                filesView.EnsureVisible(last);
            }
        }

        // Обычные стрелки, Home, End, PageUp и PageDown список обрабатывает
        // сам: он переходит по целым сообщениям и диктор читает выделенный
        // пункт целиком. Здесь только то, чего у списка нет.
        private void TranscriptKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Shift || e.Alt) return;
            if (e.Control && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                MoveBetweenSpeakers(e.KeyCode == Keys.Down ? 1 : -1);
                return;
            }
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                OpenLinkAtSelection();
                return;
            }
            if (e.Control && e.KeyCode == Keys.C)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CopySelectedMessage();
            }
        }

        private void Send()
        {
            if (busy)
            {
                statusLabel.Text = "Агент ещё отвечает. Подождите.";
                return;
            }
            string text = input.Text.Trim();
            if (text.Length == 0 && pending.Count == 0)
            {
                statusLabel.Text = "Введите сообщение или прикрепите файл.";
                return;
            }

            string model = "";
            string gguf = null;
            ModelItem mi = modelBox.SelectedItem as ModelItem;
            if (mi != null)
            {
                model = mi.Id;
                if (mi.Offline) gguf = mi.Gguf;
            }

            if (model.StartsWith("offline/", StringComparison.OrdinalIgnoreCase) && (gguf == null || gguf.Length == 0))
            {
                statusLabel.Text = "Офлайн-модель не выбрана.";
                MessageBox.Show(this,
                    "В списке выбрана офлайн-модель, но её файл не найден.\r\n"
                    + "Нажмите F3, выберите строку «Офлайн — имя файла» или добавьте файл кнопкой «Добавить модель…».",
                    "Slonobass agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
                input.Focus();
                return;
            }

            if (model.StartsWith("deepseek/", StringComparison.OrdinalIgnoreCase) && !Profile.HasKey)
            {
                statusLabel.Text = "Для модели из «Мой профиль» нужен ключ DeepSeek.";
                MessageBox.Show(this,
                    "Выбрана модель из раздела «Мой профиль», но ключ DeepSeek ещё не подключён.\r\n"
                    + "Нажмите кнопку «Мой профиль…» и вставьте свой ключ, либо выберите бесплатную модель.",
                    "Slonobass agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<FileItem> files = new List<FileItem>(pending);
            pending.Clear();
            UpdatePendingLabel();

            StringBuilder logNames = new StringBuilder();
            for (int i = 0; i < files.Count; i++)
            {
                if (i > 0) logNames.Append("; ");
                logNames.Append(files[i].Path);
            }
            Log.Write("REQUEST", "Модель: " + (model.Length > 0 ? model : "(по умолчанию)")
                + "; сессия: " + (sessionId == null ? "(новая)" : sessionId)
                + "; файлов: " + files.Count.ToString()
                + (files.Count > 0 ? " (" + logNames.ToString() + ")" : "")
                + (gguf == null ? "" : "; офлайн-файл: " + gguf)
                + "; сообщение: " + (text.Length == 0 ? "(без текста)" : text.Replace("\r\n", "\\n").Replace("\n", "\\n")));

            string time = DateTime.Now.ToString("HH:mm");
            string shownText = text.Length == 0 ? "(без текста)" : text;
            AppendBlock(userName + ", " + time, shownText, Color.FromArgb(0x1A, 0x5F, 0x1A));
            if (files.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("Прикреплено: ");
                for (int i = 0; i < files.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(files[i].Name);
                }
                AppendBlock("", sb.ToString(), Color.Black);
            }
            Sounds.Outgoing();

            if (gguf != null)
            {
                int ctx = ContextFor(gguf);
                WriteOfflineConfig(ctx);
                Log.Write("OFFLINE", "Модель " + Path.GetFileName(gguf) + ": контекст " + ctx.ToString() + " токенов.");
            }

            input.Text = "";
            SetBusy(true);
            statusLabel.Text = "Агент отвечает… 0 с";
            elapsed = 0;
            tick.Start();

            Dictionary<string, string> before = TakeSnapshot();
            string question = text;
            if (question.Length == 0)
            {
                question = files.Count == 1
                    ? "Посмотри приложенный файл."
                    : "Посмотри приложенные файлы.";
            }
            if (importContext.Length > 0)
            {
                question = importContext + "\r\n\r\nМой вопрос: " + question;
                Log.Write("IMPORT", "История из чата «" + importSource + "» добавлена в запрос: "
                    + importContext.Length.ToString() + " символов.");
                importContext = "";
                importSource = "";
            }
            Thread t = new Thread(delegate() { RunAgentPrepared(question, model, files, before, gguf); });
            t.IsBackground = true;
            t.Start();
        }

        private void RunAgentPrepared(string question, string model, List<FileItem> files,
            Dictionary<string, string> before, string gguf)
        {
            if (gguf == null)
            {
                // Обычная модель: локальный сервер больше не нужен, освобождаем память.
                StopOffline();
            }
            else
            {
                offlineError = "";
                if (!EnsureOffline(gguf))
                {
                    string err = offlineError.Length > 0
                        ? offlineError
                        : "Офлайн-модель не запустилась.";
                    Log.Write("OFFLINE-ERROR", err);
                    string shown = "offline/" + Path.GetFileNameWithoutExtension(gguf);
                    try
                    {
                        BeginInvoke(new Action(delegate()
                        {
                            Finish("", err, -1, new List<FileItem>(), shown, null);
                        }));
                    }
                    catch
                    {
                    }
                    return;
                }
            }
            RunAgent(question, model, files, before);
        }

        private void RunAgent(string question, string model, List<FileItem> files, Dictionary<string, string> before)
        {
            stopRequested = false;
            StringBuilder sb = new StringBuilder();
            StringBuilder eb = new StringBuilder();
            string localSid = null;
            int exit = -1;
            List<string> tail = new List<string>();
            int lineCount = 0;
            bool useSession = sessionId != null && sessionId.Length > 0;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                sb.Length = 0;
                eb.Length = 0;
                tail.Clear();
                lineCount = 0;
                localSid = null;
                exit = -1;

                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exePath);
                    psi.Arguments = BuildArgs(question, model, files, useSession);
                    psi.WorkingDirectory = WorkDir;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    psi.StandardOutputEncoding = Encoding.UTF8;
                    psi.StandardErrorEncoding = Encoding.UTF8;
                    Profile.Apply(psi);
                    Service.Apply(psi);

                    Log.Write("RUN", "opencode " + psi.Arguments.Replace("\r\n", "\\n").Replace("\n", "\\n")
                        + " [профиль: " + (Profile.HasKey ? "свой ключ" : "бесплатные")
                        + (Service.HasProfile ? "; сервис: " + Service.Label : "") + "]");

                    using (Process p = new Process())
                    {
                        p.StartInfo = psi;
                        p.ErrorDataReceived += delegate(object s2, DataReceivedEventArgs e)
                        {
                            if (e.Data != null)
                            {
                                lock (eb) { eb.AppendLine(e.Data); }
                            }
                        };
                        p.Start();
                        activeProc = p;
                        p.BeginErrorReadLine();

                        string line;
                        while ((line = p.StandardOutput.ReadLine()) != null)
                        {
                            lineCount++;
                            tail.Add(line);
                            if (tail.Count > 200) tail.RemoveAt(0);
                            if (line.Length < 5) continue;
                            if (localSid == null)
                            {
                                string sid = JsonValue(line, "sessionID");
                                if (sid != null && sid.Length > 0) localSid = sid;
                            }
                            if (line.StartsWith("{\"type\":\"text\"", StringComparison.Ordinal))
                            {
                                string t = JsonValue(line, "text");
                                if (t != null && t.Length > 0)
                                {
                                    if (sb.Length > 0) sb.Append("\r\n\r\n");
                                    sb.Append(t);
                                }
                            }
                            else if (line.StartsWith("{\"type\":\"error\"", StringComparison.Ordinal))
                            {
                                string em = JsonValue(line, "message");
                                if (em != null && em.Length > 0)
                                {
                                    lock (eb)
                                    {
                                        eb.AppendLine("OpenCode: " + em);
                                        // Код ответа лежит в сырой строке события, а не в
                                        // message: искать 401 надо по всей строке.
                                        if (em.IndexOf("Authentication Fails", StringComparison.OrdinalIgnoreCase) >= 0
                                            || em.IndexOf("invalid api key", StringComparison.OrdinalIgnoreCase) >= 0
                                            || line.IndexOf("\"status\":401", StringComparison.Ordinal) >= 0
                                            || line.IndexOf("UNAUTHORIZED", StringComparison.Ordinal) >= 0)
                                        {
                                            if (Service.HasProfile)
                                            {
                                                eb.AppendLine("Сервис " + Service.Label
                                                    + " отклонил ключ (код 401). Создайте новый ключ на сайте сервиса и введите его в настройках программы.");
                                            }
                                            else
                                            {
                                                eb.AppendLine("Похоже, ключ DeepSeek неверный или истёк. Откройте «Мой профиль…» и проверьте ключ.");
                                            }
                                        }
                                        else if (em.IndexOf("insufficient", StringComparison.OrdinalIgnoreCase) >= 0
                                            || em.IndexOf("balance", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            eb.AppendLine("Похоже, на балансе аккаунта DeepSeek закончились средства. Пополните баланс на platform.deepseek.com или выберите бесплатную модель.");
                                        }
                                    }
                                }
                            }
                        }
                        p.WaitForExit();
                        exit = p.ExitCode;
                    }
                }
                catch (Exception ex)
                {
                    lock (eb) { eb.AppendLine(ex.Message); }
                    Log.Exception("Запуск движка", ex);
                }
                finally
                {
                    activeProc = null;
                }

                string attemptErrors;
                lock (eb) { attemptErrors = eb.ToString(); }
                bool emptyAnswer = sb.Length == 0;
                if (stopRequested) break;
                if (useSession && attemptErrors.IndexOf("Location not found", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Сессия движка привязана к папке projects. Если программа
                    // переехала в другую папку, движок её не находит — повторяем
                    // запрос с новой сессией.
                    Log.Write("SESSION", "Движок не нашёл папку прошлой сессии — повторяю с новой сессией.");
                    sessionId = null;
                    useSession = false;
                    continue;
                }
                // Локальная модель требует строгого чередования ролей
                // «пользователь — помощник». Если разговор начат другой моделью,
                // в истории может оказаться два ответа подряд, и llama-server
                // отвечает ошибкой Jinja. Повторяем запрос новой сессией:
                // ответ всё равно пустой, терять нечего.
                if (useSession && emptyAnswer
                    && (attemptErrors.IndexOf("must alternate", StringComparison.OrdinalIgnoreCase) >= 0
                        || attemptErrors.IndexOf("Jinja", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    Log.Write("SESSION", "Модель не приняла историю разговора (чередование ролей) — повторяю с новой сессией.");
                    sessionId = null;
                    useSession = false;
                    continue;
                }
                break;
            }

            string answer = sb.ToString();
            string errors = "";
            lock (eb) { errors = eb.ToString(); }

            List<FileItem> produced = DiffSnapshot(before);

            Log.Write("RESULT", "Код выхода: " + exit.ToString()
                + "; строк вывода: " + lineCount.ToString()
                + "; ответ: " + answer.Length.ToString() + " симв."
                + "; новых файлов: " + produced.Count.ToString()
                + "; сессия: " + (localSid == null ? "(нет)" : localSid));
            if (errors.Length > 0) Log.Write("STDERR", errors.Trim());
            if (exit != 0 || answer.Length == 0)
            {
                StringBuilder rawTail = new StringBuilder();
                for (int i = 0; i < tail.Count; i++) rawTail.AppendLine(tail[i]);
                Log.Write("STDOUT-TAIL", rawTail.ToString());
            }

            try
            {
                BeginInvoke(new Action(delegate { Finish(answer, errors, exit, produced, model, localSid); }));
            }
            catch
            {
            }
        }

        private void Finish(string answer, string errors, int exit, List<FileItem> produced, string model, string sid)
        {
            tick.Stop();
            loadingOffline = false;
            bool wasStopped = stopRequested;
            stopRequested = false;
            SetBusy(false);
            if (sessionId == null && sid != null) sessionId = sid;

            string time = DateTime.Now.ToString("HH:mm");
            string answerText = answer.Trim();
            if (answerText.Length == 0)
            {
                answerText = wasStopped ? "(ответ остановлен)" : "(ответа нет)";
                if (exit != 0 && !wasStopped)
                {
                    answerText = "Не получилось получить ответ.";
                }
                if (errors.Trim().Length > 0)
                {
                    answerText += "\r\nПодробности: " + Shorten(errors.Trim(), 1200);
                }
            }
            else if (wasStopped)
            {
                answerText += "\r\n\r\n(остановлено вручную)";
            }
            lastAnswer = answerText;

            int answerIndex = messages.Count;
            AppendBlock(AgentName + ", " + time, answerText, Color.FromArgb(0x1F, 0x55, 0x9E));

            if (produced.Count > 0)
            {
                StringBuilder names = new StringBuilder();
                for (int i = 0; i < produced.Count; i++)
                {
                    if (i > 0) names.Append(", ");
                    names.Append(produced[i].Name);
                    AddFileRow(produced[i]);
                }
                AppendBlock("Файлы от агента, " + time, names.ToString(),
                    Color.FromArgb(0x7A, 0x4A, 0x00));
                statusLabel.Text = "Готово. Новых файлов: " + produced.Count.ToString() + ". Они внизу, в списке файлов.";
            }
            else
            {
                statusLabel.Text = "Готово.";
            }
            if (wasStopped)
            {
                statusLabel.Text = "Остановлено. Что успело прийти — в переписке.";
            }

            // Ответ получает фокус как выделенный пункт списка — диктор
            // прочтёт его целиком, один раз. Никаких отдельных оповещений.
            Sounds.Incoming();
            SelectMessage(answerIndex);
        }

        private void SetBusy(bool value)
        {
            busy = value;
            detachBtn.Enabled = !value && pending.Count > 0;
            newChatBtn.Enabled = !value;
            if (importBtn != null) importBtn.Enabled = !value;
            if (stopBtn != null) stopBtn.Enabled = value;
            // Выбор модели не отключаем: отключённый элемент экранный диктор
            // пропускает, и кажется, что список моделей исчез.
        }

        private void StopAgent()
        {
            if (!busy)
            {
                statusLabel.Text = "Сейчас ничего не происходит.";
                return;
            }
            stopRequested = true;
            statusLabel.Text = "Останавливаю ответ…";
            Process p = activeProc;
            if (p == null) return;
            try
            {
                // Движок может запускать дочерние процессы — гасим всё дерево.
                ProcessStartInfo kill = new ProcessStartInfo("taskkill", "/PID " + p.Id.ToString() + " /T /F");
                kill.UseShellExecute = false;
                kill.CreateNoWindow = true;
                Process.Start(kill);
            }
            catch (Exception ex)
            {
                Log.Exception("Остановка ответа", ex);
            }
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch
            {
            }
        }

        private void CopyLastAnswer()
        {
            if (transcript.Items.Count > 0)
            {
                CopySelectedMessage();
                return;
            }
            if (lastAnswer.Length == 0)
            {
                statusLabel.Text = "Копировать нечего: ответов пока нет.";
                return;
            }
            try
            {
                Clipboard.SetText(lastAnswer);
                statusLabel.Text = "Ответ скопирован в буфер обмена.";
            }
            catch (Exception ex)
            {
                Log.Exception("Копирование ответа", ex);
                statusLabel.Text = "Не удалось скопировать: " + ex.Message;
            }
        }

        // ---------- attachments ----------

        private void AttachFiles()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Прикрепить файл";
                dlg.Multiselect = true;
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                for (int i = 0; i < dlg.FileNames.Length; i++)
                {
                    string f = dlg.FileNames[i];
                    FileItem item = new FileItem();
                    item.Path = f;
                    item.Name = Path.GetFileName(f);
                    try { item.Size = new FileInfo(f).Length; } catch { item.Size = 0; }
                    item.Modified = DateTime.Now;
                    pending.Add(item);
                }
                UpdatePendingLabel();
                statusLabel.Text = "Прикреплено: " + pending.Count.ToString() + " " + FilesWord(pending.Count)
                    + ". Уйдёт вместе с сообщением.";
                input.Focus();
            }
        }

        private void ClearPending()
        {
            pending.Clear();
            UpdatePendingLabel();
            statusLabel.Text = "Прикреплённые файлы сняты";
        }

        private void UpdatePendingLabel()
        {
            if (pending.Count == 0)
            {
                attachedLabel.Text = "Файлы к сообщению: нет";
                if (detachBtn != null) detachBtn.Enabled = !busy;
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("Прикреплено: ");
            sb.Append(pending.Count.ToString());
            sb.Append(' ');
            sb.Append(FilesWord(pending.Count));
            sb.Append(" — ");
            for (int i = 0; i < pending.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(pending[i].Name);
                sb.Append(" (");
                sb.Append(SizeText(pending[i].Size));
                sb.Append(")");
            }
            attachedLabel.Text = sb.ToString();
            if (detachBtn != null) detachBtn.Enabled = !busy;
        }

        private static string FilesWord(int n)
        {
            int m10 = n % 10;
            int m100 = n % 100;
            if (m10 == 1 && m100 != 11) return "файл";
            if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return "файла";
            return "файлов";
        }

        // ---------- produced files ----------

        private Dictionary<string, string> TakeSnapshot()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            try
            {
                string[] all = Directory.GetFiles(WorkDir, "*", SearchOption.AllDirectories);
                for (int i = 0; i < all.Length; i++)
                {
                    if (IsHiddenPath(all[i])) continue;
                    try
                    {
                        FileInfo fi = new FileInfo(all[i]);
                        map[all[i]] = fi.Length.ToString() + "|" + fi.LastWriteTimeUtc.Ticks.ToString();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            return map;
        }

        private bool IsHiddenPath(string path)
        {
            string rel = path;
            if (rel.StartsWith(WorkDir, StringComparison.OrdinalIgnoreCase))
            {
                rel = rel.Substring(WorkDir.Length);
            }
            string[] parts = rel.Split('\\', '/');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string p = parts[i];
                if (p.Length == 0) continue;
                if (p.StartsWith(".", StringComparison.Ordinal)) return true;
                if (string.Equals(p, "node_modules", StringComparison.OrdinalIgnoreCase)) return true;
            }
            string name = Path.GetFileName(path);
            if (string.Equals(name, "opencode.json", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(name, "opencode.jsonc", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private List<FileItem> DiffSnapshot(Dictionary<string, string> before)
        {
            List<FileItem> list = new List<FileItem>();
            Dictionary<string, string> after = TakeSnapshot();
            foreach (KeyValuePair<string, string> kv in after)
            {
                string old;
                if (before.TryGetValue(kv.Key, out old) && old == kv.Value) continue;
                try
                {
                    FileInfo fi = new FileInfo(kv.Key);
                    FileItem item = new FileItem();
                    item.Path = kv.Key;
                    item.Name = Path.GetFileName(kv.Key);
                    item.Size = fi.Length;
                    item.Modified = fi.LastWriteTime;
                    list.Add(item);
                }
                catch
                {
                }
            }
            list.Sort(delegate(FileItem a, FileItem b) { return a.Modified.CompareTo(b.Modified); });
            return list;
        }

        private void AddFileRow(FileItem item)
        {
            for (int i = 0; i < filesView.Items.Count; i++)
            {
                FileItem ex = filesView.Items[i].Tag as FileItem;
                if (ex != null && string.Equals(ex.Path, item.Path, StringComparison.OrdinalIgnoreCase))
                {
                    ex.Size = item.Size;
                    ex.Modified = item.Modified;
                    filesView.Items[i].SubItems[1].Text = item.Modified.ToString("HH:mm");
                    filesView.Items[i].SubItems[2].Text = SizeText(item.Size);
                    return;
                }
            }
            ListViewItem row = new ListViewItem(item.Name);
            row.Tag = item;
            row.SubItems.Add(item.Modified.ToString("HH:mm"));
            row.SubItems.Add(SizeText(item.Size));
            filesView.Items.Add(row);
            row.Selected = true;
        }

        private void FilesViewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SaveSelected();
            }
        }

        private void SaveSelected()
        {
            FileItem item = SelectedFile();
            if (item == null)
            {
                statusLabel.Text = "Сначала выберите файл в списке.";
                return;
            }
            try
            {
                string dest = UniquePath(downloadsDir, item.Name);
                File.Copy(item.Path, dest, false);
                Log.Write("SAVE", item.Path + " -> " + dest);
                statusLabel.Text = "Сохранено в Загрузки: " + Path.GetFileName(dest);
                Sounds.Saved();
            }
            catch (Exception ex)
            {
                Log.Exception("Сохранить " + item.Path, ex);
                statusLabel.Text = "Не удалось сохранить: " + ex.Message;
            }
        }

        private void OpenSelected()
        {
            FileItem item = SelectedFile();
            if (item == null)
            {
                statusLabel.Text = "Сначала выберите файл в списке.";
                return;
            }
            try
            {
                Process.Start(item.Path);
            }
            catch (Exception ex)
            {
                Log.Exception("Открыть файл " + item.Path, ex);
                statusLabel.Text = "Не удалось открыть: " + ex.Message;
            }
        }

        private void OpenLogFolder()
        {
            try
            {
                if (Log.Folder == null)
                {
                    statusLabel.Text = "Папка журнала недоступна.";
                    return;
                }
                Process.Start("explorer.exe", "\"" + Log.Folder + "\"");
                statusLabel.Text = "Открыта папка журнала: " + Log.Folder;
            }
            catch (Exception ex)
            {
                Log.Exception("Открыть журнал", ex);
                statusLabel.Text = "Не удалось открыть журнал: " + ex.Message;
            }
        }

        private void OpenProfile()
        {
            using (ProfileForm f = new ProfileForm())
            {
                DialogResult r = f.ShowDialog(this);
                if (r != DialogResult.OK) return;
                if (Profile.HasKey)
                {
                    statusLabel.Text = "Мой профиль DeepSeek подключён: " + Profile.Hint;
                    Log.Write("PROFILE", "Ключ DeepSeek сохранён (" + Profile.Hint + ")");
                }
                else
                {
                    statusLabel.Text = "Мой профиль DeepSeek отключён. Работают бесплатные модели.";
                    Log.Write("PROFILE", "Ключ DeepSeek удалён");
                }
                LoadModels();
            }
        }

        private void OpenOtherService()
        {
            using (ServiceForm f = new ServiceForm())
            {
                DialogResult r = f.ShowDialog(this);
                if (r != DialogResult.OK) return;
                WriteOfflineConfig();
                if (Service.HasProfile)
                {
                    statusLabel.Text = Service.Label + " подключён: " + Service.Hint
                        + ". Модели в списке моделей.";
                    Log.Write("SERVICE", Service.Label + " сохранён (" + Service.Hint
                        + "), моделей: " + Service.Models.Length.ToString());
                }
                else
                {
                    statusLabel.Text = "Другой сервис отключён.";
                    Log.Write("SERVICE", "Другой сервис отключён");
                }
                LoadModels();
            }
        }

        private FileItem SelectedFile()
        {
            if (filesView.SelectedItems.Count == 0) return null;
            return filesView.SelectedItems[0].Tag as FileItem;
        }

        private static string UniquePath(string dir, string name)
        {
            string full = Path.Combine(dir, name);
            if (!File.Exists(full)) return full;
            string baseName = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            for (int i = 2; i < 1000; i++)
            {
                full = Path.Combine(dir, baseName + " (" + i.ToString() + ")" + ext);
                if (!File.Exists(full)) return full;
            }
            return Path.Combine(dir, baseName + " (" + DateTime.Now.Ticks.ToString() + ")" + ext);
        }

        // ---------- models ----------

        private void LoadModels()
        {
            Thread t = new Thread(delegate()
            {
                List<string> found = new List<string>();
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exePath);
                    psi.Arguments = "models --standalone";
                    psi.WorkingDirectory = WorkDir;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    psi.StandardOutputEncoding = Encoding.UTF8;
                    psi.StandardErrorEncoding = Encoding.UTF8;
                    Profile.Apply(psi);
                    Service.Apply(psi);
                    using (Process p = Process.Start(psi))
                    {
                        string outp = p.StandardOutput.ReadToEnd();
                        string errp = p.StandardError.ReadToEnd();
                        p.WaitForExit();
                        if (errp.Trim().Length > 0) Log.Write("MODELS-STDERR", errp.Trim());
                        string[] lines = outp.Split('\n');
                        for (int i = 0; i < lines.Length; i++)
                        {
                            string s = lines[i].Trim();
                            if (s.Length == 0) continue;
                            if (s.IndexOf('/') < 0) continue;
                            if (s.IndexOf(' ') >= 0) continue;
                            if (s.IndexOf("embed", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            if (!found.Contains(s)) found.Add(s);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception("Список моделей", ex);
                }
                Log.Write("MODELS", "Найдено моделей: " + found.Count.ToString()
                    + "; профиль DeepSeek: " + (Profile.HasKey ? "подключён " + Profile.Hint : "нет")
                    + "; другой сервис: " + (Service.HasProfile ? Service.Label + " " + Service.Hint : "нет"));
                try
                {
                    BeginInvoke(new Action(delegate { FillModels(found); }));
                }
                catch
                {
                }
                if (Service.HasProfile)
                {
                    // Проверка ключа сразу при запуске: без неё 401 всплывает
                    // только в момент первого сообщения и выглядит загадочно.
                    string probe = Service.ProbeKey(Service.BaseUrl, Service.Key,
                        Service.Models.Length > 0 ? Service.Models[0] : "");
                    if (probe.Length == 0)
                    {
                        Log.Write("SERVICE", "Ключ " + Service.Label + " принят сервисом");
                    }
                    else
                    {
                        Log.Write("SERVICE", "Ключ " + Service.Label + " не принят: " + probe);
                    }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private static string KeyOf(ModelItem m)
        {
            if (m == null) return "";
            if (m.Offline && m.Gguf != null && m.Gguf.Length > 0) return "offline:" + m.Gguf;
            return m.Id == null ? "" : m.Id;
        }

        private void FillModels(List<string> found)
        {
            if (found == null) found = new List<string>();
            if (found.Count == 0)
            {
                for (int i = 0; i < FallbackModels.Length; i++) found.Add(FallbackModels[i]);
            }
            lastFound = new List<string>(found);

            string keep = "";
            ModelItem cur = modelBox.SelectedItem as ModelItem;
            if (cur != null) keep = KeyOf(cur);
            if (keep.Length == 0) keep = preferredModel;
            if (keep.Length == 0) keep = currentModelKey;

            List<string> mine = new List<string>();
            List<string> others = new List<string>();
            List<string> free = new List<string>();
            for (int i = 0; i < found.Count; i++)
            {
                // Провайдер offline движок показывает в своём списке, но пункты
                // для офлайн-файлов строим сами: у движка это одна модель без файла.
                if (found[i].StartsWith("offline/", StringComparison.OrdinalIgnoreCase)) continue;
                if (found[i].StartsWith("deepseek/", StringComparison.OrdinalIgnoreCase)) mine.Add(found[i]);
                else if (found[i].StartsWith("other/", StringComparison.OrdinalIgnoreCase)) others.Add(found[i]);
                else free.Add(found[i]);
            }
            if (Profile.HasKey && mine.Count == 0)
            {
                for (int i = 0; i < ApiModels.Length; i++) mine.Add(ApiModels[i]);
            }
            if (!Profile.HasKey) mine.Clear();

            List<string> ggufs = LoadOfflinePaths();

            suppressModelSwitch = true;
            modelBox.Items.Clear();
            for (int i = 0; i < ggufs.Count; i++)
            {
                ModelItem oi = new ModelItem("offline/local",
                    "Офлайн — " + Path.GetFileName(ggufs[i]), false);
                oi.Offline = true;
                oi.Gguf = ggufs[i];
                modelBox.Items.Add(oi);
            }
            for (int i = 0; i < mine.Count; i++)
            {
                modelBox.Items.Add(new ModelItem(mine[i], "Мой профиль — " + ShortName(mine[i]), true));
            }
            string serviceLabel = Service.HasProfile ? Service.Label : "Другой сервис";
            for (int i = 0; i < others.Count; i++)
            {
                modelBox.Items.Add(new ModelItem(others[i], serviceLabel + " — " + ShortName(others[i]), true));
            }
            for (int i = 0; i < free.Count; i++) modelBox.Items.Add(new ModelItem(free[i]));

            int index = 0;
            bool matched = false;
            if (keep.Length > 0)
            {
                for (int i = 0; i < modelBox.Items.Count; i++)
                {
                    ModelItem m = modelBox.Items[i] as ModelItem;
                    if (m != null && string.Equals(KeyOf(m), keep, StringComparison.Ordinal)) { index = i; matched = true; break; }
                }
            }
            if (!matched)
            {
                index = -1;
                for (int i = 0; i < modelBox.Items.Count; i++)
                {
                    ModelItem m = modelBox.Items[i] as ModelItem;
                    if (m != null && string.Equals(m.Id, "opencode/deepseek-v4.1-flash-free", StringComparison.Ordinal)) { index = i; break; }
                }
                if (index < 0)
                {
                    for (int i = 0; i < modelBox.Items.Count; i++)
                    {
                        ModelItem m = modelBox.Items[i] as ModelItem;
                        if (m != null && !m.Mine && !m.Offline) { index = i; break; }
                    }
                }
                if (index < 0) index = 0;
            }
            if (modelBox.Items.Count > 0) modelBox.SelectedIndex = index;
            suppressModelSwitch = false;

            StringBuilder desc = new StringBuilder();
            if (ggufs.Count > 0)
                desc.Append("Сверху офлайн-модели (" + ggufs.Count.ToString() + ") — файлы .gguf с этого компьютера. ");
            if (Profile.HasKey)
                desc.Append("Раздел «Мой профиль» — модели вашего аккаунта DeepSeek (" + mine.Count.ToString() + "). ");
            if (others.Count > 0)
                desc.Append("Раздел «" + serviceLabel + "» — модели другого сервиса (" + others.Count.ToString() + "). ");
            desc.Append("Ниже бесплатные модели. Кнопка «Добавить модель» добавляет файл .gguf. "
                + "У каждой модели свой чат: при смене модели открывается её переписка. "
                + "Alt+I — импорт истории из чата другой модели.");
            modelBox.AccessibleDescription = desc.ToString();

            AlignChatToSelection(true);
        }

        // ---------- офлайн-модели (.gguf через llama.cpp) ----------

        private List<string> LoadOfflinePaths()
        {
            List<string> res = new List<string>();
            try
            {
                if (!File.Exists(modelsFile)) return res;
                string[] lines = File.ReadAllLines(modelsFile, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string s = lines[i].Trim();
                    if (s.Length == 0) continue;
                    if (!File.Exists(s))
                    {
                        Log.Write("OFFLINE", "Пропущен отсутствующий файл модели: " + s);
                        continue;
                    }
                    bool dup = false;
                    for (int j = 0; j < res.Count; j++)
                    {
                        if (string.Equals(res[j], s, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    }
                    if (!dup) res.Add(s);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("Список офлайн-моделей", ex);
            }
            return res;
        }

        private void SaveOfflinePaths(List<string> paths)
        {
            try
            {
                string dir = Path.GetDirectoryName(modelsFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(modelsFile, paths.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Exception("Сохранение офлайн-моделей", ex);
            }
        }

        private void AddOfflineModel()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Добавить офлайн-модель";
                dlg.Filter = "Модели GGUF (*.gguf)|*.gguf|Все файлы (*.*)|*.*";
                dlg.Multiselect = true;
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                List<string> paths = LoadOfflinePaths();
                int added = 0;
                string last = "";
                for (int i = 0; i < dlg.FileNames.Length; i++)
                {
                    string p = dlg.FileNames[i];
                    bool dup = false;
                    for (int j = 0; j < paths.Count; j++)
                    {
                        if (string.Equals(paths[j], p, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    }
                    if (dup) continue;
                    paths.Add(p);
                    added++;
                    last = p;
                }
                if (added > 0) SaveOfflinePaths(paths);
                Log.Write("OFFLINE", "Добавлено моделей: " + added.ToString()
                    + "; всего в списке: " + paths.Count.ToString());

                if (added > 0)
                {
                    preferredModel = "offline:" + last;
                    modelBox.SelectedIndex = -1;
                }
                FillModels(lastFound);
                if (added > 0) SaveHistory();

                statusLabel.Text = added > 0
                    ? "Добавлено офлайн-моделей: " + added.ToString() + ". Выбрана: " + Path.GetFileName(last)
                    : "Эта модель уже есть в списке.";
                input.Focus();
            }
        }

        private static readonly Dictionary<string, int> ctxCache = new Dictionary<string, int>();

        // Сколько токенов контекста дать модели: берём из её метаданных,
        // ограничиваем обученным контекстом и свободной памятью.
        private static int ContextFor(string gguf)
        {
            lock (ctxCache)
            {
                int known;
                if (ctxCache.TryGetValue(gguf, out known)) return known;
            }
            int ctx = 8192;
            int train;
            long kvPerToken;
            if (Gguf.TryRead(gguf, out train, out kvPerToken))
            {
                long fileSize = 0;
                try { fileSize = new FileInfo(gguf).Length; } catch { }
                long ram = Gguf.TotalRamBytes();
                long budget = ram / 2 - fileSize;
                if (budget < 512L * 1024 * 1024) budget = 512L * 1024 * 1024;
                long byRam = kvPerToken > 0 ? budget / kvPerToken : 32768;
                int[] steps = new int[] { 2048, 4096, 8192, 16384, 32768, 65536, 131072 };
                ctx = 2048;
                for (int i = 0; i < steps.Length; i++)
                {
                    if (steps[i] <= train && steps[i] <= byRam) ctx = steps[i];
                }
                Log.Write("OFFLINE", "Метаданные " + Path.GetFileName(gguf)
                    + ": обученный контекст " + train.ToString()
                    + ", кэш " + (kvPerToken / 1024).ToString() + " КБ на 1000 токенов"
                    + ", оперативной памяти " + (ram / (1024L * 1024L)).ToString() + " МБ"
                    + ", выбран контекст " + ctx.ToString());
            }
            else
            {
                Log.Write("OFFLINE", "Метаданные GGUF прочитать не удалось — беру осторожные 8192 токена: " + gguf);
            }
            lock (ctxCache) { ctxCache[gguf] = ctx; }
            return ctx;
        }

        private void WriteOfflineConfig(int ctx = 8192)
        {
            int limitCtx = Math.Max(1024, ctx - 1024);
            int output = Math.Min(4096, Math.Max(1024, limitCtx / 2));
            try
            {
                string path = Path.Combine(WorkDir, "opencode.json");
                string url = "http://127.0.0.1:" + OfflinePort.ToString() + "/v1";
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"$schema\": \"https://opencode.ai/config.json\",");
                sb.AppendLine("  \"model\": \"opencode/deepseek-v4.1-flash-free\",");
                // В неинтерактивном запуске движок отклоняет всё, что требует
                // подтверждения («Cancelled a request for input this run cannot
                // answer»). Встроенный веб-поиск сам просит подтверждение даже
                // при allow, поэтому он выключен, а загрузка страниц разрешена:
                // поиск идёт через сайты поиска, открытые webfetch.
                sb.AppendLine("  \"permission\": {");
                sb.AppendLine("    \"question\": \"deny\",");
                sb.AppendLine("    \"websearch\": \"deny\",");
                sb.AppendLine("    \"webfetch\": \"allow\"");
                sb.AppendLine("  },");
                sb.AppendLine("  \"provider\": {");
                sb.AppendLine("    \"offline\": {");
                sb.AppendLine("      \"npm\": \"@ai-sdk/openai-compatible\",");
                sb.AppendLine("      \"name\": \"Офлайн\",");
                sb.AppendLine("      \"api\": \"" + url + "\",");
                sb.AppendLine("      \"options\": { \"baseURL\": \"" + url + "\", \"apiKey\": \"local\" },");
                sb.AppendLine("      \"models\": {");
                sb.AppendLine("        \"local\": {");
                sb.AppendLine("          \"id\": \"local\",");
                sb.AppendLine("          \"name\": \"Локальная модель\",");
                sb.AppendLine("          \"tool_call\": true,");
                sb.AppendLine("          \"limit\": { \"context\": " + limitCtx.ToString() + ", \"output\": " + output.ToString() + " }");
                sb.AppendLine("        }");
                sb.AppendLine("      }");
                bool other = Service.HasProfile && Service.Models.Length > 0;
                sb.AppendLine(other ? "    }," : "    }");
                if (other)
                {
                    string baseUrl = Service.BaseUrl.TrimEnd('/');
                    sb.AppendLine("    \"other\": {");
                    sb.AppendLine("      \"npm\": \"@ai-sdk/openai-compatible\",");
                    sb.AppendLine("      \"name\": \"" + JsonEscape(Service.Label) + "\",");
                    sb.AppendLine("      \"api\": \"" + JsonEscape(baseUrl) + "\",");
                    sb.AppendLine("      \"options\": { \"baseURL\": \"" + JsonEscape(baseUrl)
                        + "\", \"apiKey\": \"{env:POLZA_API_KEY}\" },");
                    sb.AppendLine("      \"models\": {");
                    string[] ids = Service.Models;
                    for (int i = 0; i < ids.Length; i++)
                    {
                        string id = JsonEscape(ids[i]);
                        sb.AppendLine("        \"" + id + "\": { \"id\": \"" + id
                            + "\", \"name\": \"" + id + "\", \"tool_call\": true }"
                            + (i + 1 < ids.Length ? "," : ""));
                    }
                    sb.AppendLine("      }");
                    sb.AppendLine("    }");
                }
                sb.AppendLine("  }");
                sb.AppendLine("}");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                WriteAgentRules();
                Log.Write("OFFLINE", "Конфиг движка записан: " + path);
            }
            catch (Exception ex)
            {
                Log.Exception("Конфиг движка", ex);
            }
        }

        // Правила для модели. Без них она при недоступном веб-поиске просто
        // отвечает «не нашёл» и перестаёт искать другими способами.
        private void WriteAgentRules()
        {
            const string marker = "<!-- Slonobass agent -->";
            try
            {
                string path = Path.Combine(WorkDir, "AGENTS.md");
                if (File.Exists(path))
                {
                    string head = "";
                    using (StreamReader r = new StreamReader(path, Encoding.UTF8))
                    {
                        char[] buf = new char[200];
                        int n = r.Read(buf, 0, buf.Length);
                        head = new string(buf, 0, n);
                    }
                    if (head.IndexOf(marker, StringComparison.Ordinal) < 0) return;
                }
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(marker);
                sb.AppendLine("# Правила работы");
                sb.AppendLine();
                sb.AppendLine("Ты — помощник внутри программы Slonobass agent. Отвечай по-русски, коротко и по делу.");
                sb.AppendLine();
                sb.AppendLine("## Поиск в интернете");
                sb.AppendLine("Встроенного поиска нет: инструмент websearch отключён и вызывать его не нужно. Ищи через webfetch.");
                sb.AppendLine("1. Открой страницу поиска:");
                sb.AppendLine("   https://lite.duckduckgo.com/lite/?q=ЗАПРОС (вместо пробелов поставь +)");
                sb.AppendLine("2. Если не открылось — https://www.bing.com/search?q=ЗАПРОС");
                sb.AppendLine("3. Ссылки из выдачи открывай через webfetch и читай текст страницы, а не догадывайся по названию.");
                sb.AppendLine("4. Если в вопросе есть название сервиса или адрес сайта — попробуй сразу открыть этот сайт через webfetch.");
                sb.AppendLine("5. Если не удалось ничего — честно скажи, что поиск сейчас не работает. Не выдумывай факты, цены и ссылки.");
                sb.AppendLine();
                sb.AppendLine("## Файлы");
                sb.AppendLine("Новые файлы создавай только тогда, когда об этом прямо просят.");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Exception("Правила для модели", ex);
            }
        }

        private static string JsonEscape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool HttpOk(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Proxy = null;
                req.Timeout = 2500;
                req.ReadWriteTimeout = 2500;
                req.KeepAlive = false;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    return (int)resp.StatusCode == 200;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string HttpGetText(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null;
            req.Timeout = 3000;
            req.ReadWriteTimeout = 3000;
            req.KeepAlive = false;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        // llama-server отдаёт в /props контекст одного слота (n_ctx).
        private int OfflineSlotContext()
        {
            try
            {
                string body = HttpGetText("http://127.0.0.1:" + OfflinePort.ToString() + "/props");
                Match m = Regex.Match(body, "\"n_ctx\"\\s*:\\s*(\\d+)");
                if (m.Success)
                {
                    int v;
                    if (int.TryParse(m.Groups[1].Value, out v) && v > 0 && v <= 1048576) return v;
                }
            }
            catch
            {
            }
            return 0;
        }

        private bool OfflineReady()
        {
            string b = "http://127.0.0.1:" + OfflinePort.ToString();
            if (HttpOk(b + "/health")) return true;
            return HttpOk(b + "/v1/models");
        }

        private void SetStatusSafe(string text)
        {
            try
            {
                BeginInvoke(new Action(delegate() { statusLabel.Text = text; }));
            }
            catch
            {
            }
        }

        // ---------- обновление программы ----------

        private static string UpdateFolder
        {
            get { return Path.Combine(Path.GetTempPath(), "SlonobassAgent-update"); }
        }

        private void CheckForUpdates()
        {
            if (updating) return;
            if (busy)
            {
                MessageBox.Show(this,
                    "Сначала дождитесь ответа агента или остановите его: пока идёт ответ, файлы программы заняты.",
                    "Slonobass agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            updating = true;
            updateBtn.Enabled = false;
            statusLabel.Text = "Проверяю обновление…";
            Thread t = new Thread(delegate()
            {
                int latest = 0;
                string zipUrl = null;
                string exeUrl = null;
                string error = null;
                try
                {
                    string json = FetchReleaseList();
                    FindLatestRelease(json, out latest, out zipUrl, out exeUrl);
                    if (latest == 0) error = "на сервере не нашлось файла обновления.";
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Log.Exception("Проверка обновления", ex);
                }
                try
                {
                    BeginInvoke(new Action(delegate() { AfterUpdateCheck(latest, zipUrl, exeUrl, error); }));
                }
                catch
                {
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void AfterUpdateCheck(int latest, string zipUrl, string exeUrl, string error)
        {
            if (error != null)
            {
                ResetUpdate("Не удалось проверить обновление.");
                MessageBox.Show(this,
                    "Не удалось проверить обновление: " + error
                    + "\r\n\r\nПроверьте подключение к интернету и попробуйте ещё раз.",
                    "Slonobass agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (latest <= AppVersion)
            {
                ResetUpdate("Обновлений нет.");
                MessageBox.Show(this,
                    "Установлена последняя версия программы: " + AppVersion.ToString() + ".",
                    "Slonobass agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult r = MessageBox.Show(this,
                "Доступна версия " + latest.ToString() + ". Сейчас установлена " + AppVersion.ToString() + "."
                + "\r\n\r\nСкачать обновление сейчас? Программа закроется, установит новую версию и запустится снова.",
                "Slonobass agent", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes)
            {
                ResetUpdate("Обновление отложено.");
                return;
            }
            statusLabel.Text = "Скачиваю обновление…";
            Thread t = new Thread(delegate() { DownloadAndApply(exeUrl, zipUrl, latest); });
            t.IsBackground = true;
            t.Start();
        }

        private void DownloadAndApply(string exeUrl, string zipUrl, int version)
        {
            string error = null;
            try
            {
                string folder = UpdateFolder;
                try
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                }
                catch
                {
                }
                string staging = Path.Combine(folder, "new");
                Directory.CreateDirectory(staging);
                // Сначала маленький файл программы: десятки килобайт вместо
                // полутора сотен мегабайт — на слабом или фильтрующем
                // подключении такой запрос проходит, а большой архив рвётся.
                bool ready = false;
                if (exeUrl != null)
                {
                    string exePath = Path.Combine(staging, "Slonobass-Agent.exe");
                    try
                    {
                        DownloadTo(exeUrl, exePath);
                        if (!LooksLikeExe(exePath))
                            throw new IOException("скачанный файл программы повреждён.");
                        ready = true;
                        Log.Write("UPDATE", "Обновление " + version.ToString()
                            + " скачано по короткому пути (только программа): " + exePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Exception("Скачивание файла программы", ex);
                        try { File.Delete(exePath); } catch { }
                    }
                }
                if (!ready)
                {
                    if (zipUrl == null) throw new IOException("на сервере нет файла обновления.");
                    string zipPath = Path.Combine(folder, "update.zip");
                    DownloadTo(zipUrl, zipPath);
                    SetStatusSafe("Распаковываю обновление…");
                    ZipFile.ExtractToDirectory(zipPath, staging);
                    try { File.Delete(zipPath); } catch { }
                    Log.Write("UPDATE", "Обновление " + version.ToString() + " скачано и распаковано: " + staging);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Exception("Скачивание обновления", ex);
            }
            try
            {
                BeginInvoke(new Action(delegate() { AfterDownload(error); }));
            }
            catch
            {
            }
        }

        private void AfterDownload(string error)
        {
            if (error != null)
            {
                ResetUpdate("Не удалось скачать обновление.");
                MessageBox.Show(this,
                    "Не удалось скачать обновление: " + error
                    + "\r\n\r\nПроверьте подключение к интернету и попробуйте ещё раз.",
                    "Slonobass agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string install = AppDomain.CurrentDomain.BaseDirectory;
            if (!CanWriteTo(install))
            {
                ResetUpdate("Нет прав записи в папку программы.");
                MessageBox.Show(this,
                    "Программа лежит в папке, куда нет прав записи:\r\n" + install
                    + "\r\n\r\nПеренесите папку программы туда, где можно записывать (например, в «Документы»),"
                    + " и повторите обновление.",
                    "Slonobass agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                StopOffline();
                // Помощник — копия этой же программы во временной папке. Он
                // дождётся закрытия текущей, заменит файлы и запустит программу.
                string helper = Path.Combine(UpdateFolder, "Slonobass-Agent-update.exe");
                File.Copy(Application.ExecutablePath, helper, true);
                ProcessStartInfo psi = new ProcessStartInfo(helper);
                psi.Arguments = "--apply-update \"" + Path.Combine(UpdateFolder, "new") + "\" \""
                    + install.TrimEnd('\\') + "\" " + Process.GetCurrentProcess().Id.ToString();
                psi.WorkingDirectory = UpdateFolder;
                psi.UseShellExecute = false;
                Process.Start(psi);
                statusLabel.Text = "Устанавливаю обновление…";
                Log.Write("UPDATE", "Запущен помощник обновления; программа закрывается.");
                Application.Exit();
            }
            catch (Exception ex)
            {
                ResetUpdate("Не удалось запустить обновление.");
                MessageBox.Show(this,
                    "Не удалось запустить обновление: " + ex.Message,
                    "Slonobass agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ResetUpdate(string status)
        {
            updating = false;
            if (updateBtn != null) updateBtn.Enabled = true;
            statusLabel.Text = status;
        }

        private static string FetchReleaseList()
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ReleasesApi);
            req.Proxy = null;
            req.UserAgent = "SlonobassAgent/" + AppVersion.ToString();
            req.Accept = "application/vnd.github+json";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.KeepAlive = false;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        // В ответе GitHub — список релизов, новые сверху. У каждого тег вида
        // slonobass-agent-N и рядом ссылка на zip. Берём самый большой номер,
        // у которого есть zip: порядок ответа может измениться.
        private static void FindLatestRelease(string json, out int latest, out string zipUrl, out string exeUrl)
        {
            latest = 0;
            zipUrl = null;
            exeUrl = null;
            if (json == null) return;
            MatchCollection tags = Regex.Matches(json, "\"tag_name\"\\s*:\\s*\"slonobass-agent-(\\d+)\"");
            for (int i = 0; i < tags.Count; i++)
            {
                int n;
                if (!int.TryParse(tags[i].Groups[1].Value, out n)) continue;
                if (n <= latest) continue;
                int start = tags[i].Index;
                int end = i + 1 < tags.Count ? tags[i + 1].Index : json.Length;
                string block = json.Substring(start, end - start);
                Match zip = Regex.Match(block,
                    "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\\.zip\"");
                if (!zip.Success) continue;
                Match exe = Regex.Match(block,
                    "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\\.exe\"");
                latest = n;
                zipUrl = zip.Groups[1].Value.Replace("\\/", "/");
                exeUrl = exe.Success ? exe.Groups[1].Value.Replace("\\/", "/") : null;
            }
        }

        private static bool LooksLikeExe(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Length < 20000) return false;
                using (FileStream s = File.OpenRead(path))
                {
                    int a = s.ReadByte();
                    int b = s.ReadByte();
                    return a == 'M' && b == 'Z';
                }
            }
            catch
            {
                return false;
            }
        }

        // Загрузка с повторами: соединение с сервером файлов иногда рвётся
        // (фильтры сети, антивирус), и вторая попытка обычно проходит.
        private void DownloadTo(string url, string path)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    // Первая попытка — как настроено в системе (вдруг у человека
                    // свой прокси для таких сайтов), вторая — напрямую.
                    DownloadOnce(url, path, attempt == 1);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    Log.Write("UPDATE", "Попытка " + (attempt + 1).ToString() + " не удалась: " + ex.Message);
                    if (attempt + 1 < 3)
                    {
                        SetStatusSafe("Скачиваю обновление… повтор " + (attempt + 2).ToString() + " из 3");
                        Thread.Sleep(2000);
                    }
                }
            }
            throw last;
        }

        private void DownloadOnce(string url, string path, bool noProxy)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            if (noProxy) req.Proxy = null;
            req.UserAgent = "SlonobassAgent/" + AppVersion.ToString();
            req.Timeout = 30000;
            req.ReadWriteTimeout = 120000;
            req.KeepAlive = false;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream input = resp.GetResponseStream())
            using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long total = resp.ContentLength;
                byte[] buffer = new byte[81920];
                long done = 0;
                int lastPercent = -1;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    done += read;
                    if (total > 0)
                    {
                        int percent = (int)(done * 100 / total);
                        if (percent != lastPercent && percent % 5 == 0)
                        {
                            lastPercent = percent;
                            SetStatusSafe("Скачиваю обновление… " + percent.ToString() + "%");
                        }
                    }
                }
                if (total > 0 && done != total)
                    throw new IOException("файл скачан не полностью (" + done.ToString() + " из " + total.ToString() + " байт).");
            }
        }

        private static bool CanWriteTo(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (FileStream s = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    s.WriteByte(0);
                }
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void StopOffline()
        {
            lock (llamaLock) StopOfflineLocked();
        }

        private void StopOfflineLocked()
        {
            if (llamaProc == null) return;
            try
            {
                if (!llamaProc.HasExited)
                {
                    llamaProc.Kill();
                    llamaProc.WaitForExit(5000);
                }
            }
            catch
            {
            }
            try { llamaProc.Dispose(); } catch { }
            llamaProc = null;
            llamaLoaded = null;
            loadingOffline = false;
            Log.Write("OFFLINE", "Сервер llama остановлен.");
        }

        // Запускает llama-server с выбранным файлом .gguf и ждёт готовности.
        private bool EnsureOffline(string gguf)
        {
            if (gguf == null || !File.Exists(gguf))
            {
                offlineError = "Файл офлайн-модели не найден: "
                    + (gguf == null ? "(путь потерян)" : gguf)
                    + "\r\nДобавьте модель заново кнопкой «Добавить модель…».";
                return false;
            }

            bool alive = false;
            lock (llamaLock)
            {
                if (llamaProc != null && !llamaProc.HasExited
                    && string.Equals(llamaLoaded, gguf, StringComparison.OrdinalIgnoreCase))
                {
                    alive = true;
                }
                else
                {
                    StopOfflineLocked();
                }
            }
            if (alive && OfflineReady())
            {
                int running = OfflineSlotContext();
                if (running > 0) WriteOfflineConfig(Math.Min(ContextFor(gguf), running));
                return true;
            }
            if (alive)
            {
                // Процесс жив, но сервер не отвечает — начинаем заново.
                lock (llamaLock) StopOfflineLocked();
            }

            string exe = Path.Combine(llamaDir, "llama-server.exe");
            if (!File.Exists(exe))
            {
                offlineError = "Рядом с программой нет папки engine\\llama с файлом llama-server.exe."
                    + "\r\nПохоже, архив распакован не полностью. Распакуйте архив заново в отдельную папку.";
                return false;
            }

            int ctx = ContextFor(gguf);
            SetStatusSafe("Запускаю офлайн-модель, контекст " + ctx.ToString() + " токенов…");
            Log.Write("OFFLINE", "Запуск llama-server: контекст " + ctx.ToString() + " токенов, файл " + gguf);
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.Arguments = "-m " + Quote(gguf)
                + " --host 127.0.0.1 --port " + OfflinePort.ToString()
                + " -c " + ctx.ToString() + " --parallel 1 -np 1 --alias local --jinja";
            psi.WorkingDirectory = llamaDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            Process p = new Process();
            p.StartInfo = psi;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs a)
            {
                if (a.Data != null) Log.Write("LLAMA-OUT", Shorten(a.Data, 500));
            };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs a)
            {
                if (a.Data != null) Log.Write("LLAMA-ERR", Shorten(a.Data, 500));
            };
            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                offlineError = "Не удалось запустить llama-server.exe: " + ex.Message
                    + "\r\nПодробности — в «Журнале ошибок».";
                Log.Exception("Запуск llama-server", ex);
                try { p.Dispose(); } catch { }
                return false;
            }

            lock (llamaLock)
            {
                llamaProc = p;
                llamaLoaded = gguf;
            }
            loadingOffline = true;
            Log.Write("OFFLINE", "Сервер llama запущен для файла: " + gguf
                + "; порт " + OfflinePort.ToString());

            int waited = 0;
            while (waited < 300)
            {
                if (p.HasExited)
                {
                    int code = p.ExitCode;
                    ReleaseLocal(p);
                    offlineError = "llama-server завершился с кодом " + code.ToString()
                        + ", не дождавшись загрузки модели."
                        + "\r\nЧастые причины: файл модели повреждён или не подходит этой версии llama.cpp,"
                        + " либо не хватает оперативной памяти."
                        + "\r\nПодробности — в «Журнале ошибок», разделы LLAMA.";
                    return false;
                }
                if (OfflineReady())
                {
                    loadingOffline = false;
                    // Сервер мог разделить контекст между слотами: движку
                    // сообщаем реально доступный размер, иначе он ошибётся
                    // «request exceeds the available context size».
                    int slotCtx = OfflineSlotContext();
                    int useCtx = slotCtx > 0 && slotCtx < ctx ? slotCtx : ctx;
                    WriteOfflineConfig(useCtx);
                    Log.Write("OFFLINE", "Контекст слота: " + slotCtx.ToString()
                        + "; движку выставлен " + useCtx.ToString() + ".");
                    SetStatusSafe("Офлайн-модель готова (контекст " + useCtx.ToString() + "), отправляю запрос…");
                    Log.Write("OFFLINE", "Сервер llama готов после " + waited.ToString() + " с.");
                    return true;
                }
                Thread.Sleep(1000);
                waited++;
            }

            ReleaseLocal(p);
            offlineError = "Модель не загрузилась за 5 минут."
                + "\r\nБольшие модели на слабом компьютере грузятся долго; попробуйте файл поменьше (Q4)."
                + "\r\nПодробности — в «Журнале ошибок», разделы LLAMA.";
            return false;
        }

        private void ReleaseLocal(Process p)
        {
            lock (llamaLock)
            {
                if (llamaProc == p)
                {
                    StopOfflineLocked();
                    return;
                }
            }
            try { p.Dispose(); } catch { }
        }

        private static string ShortName(string id)
        {
            int slash = id.IndexOf('/');
            if (slash >= 0 && slash + 1 < id.Length) return id.Substring(slash + 1);
            return id;
        }

        // ---------- process helpers ----------

        private string BuildArgs(string question, string model, List<FileItem> files, bool useSession)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("run --standalone --auto --format json");
            if (useSession && sessionId != null && sessionId.Length > 0)
            {
                sb.Append(" --session ");
                sb.Append(Quote(sessionId));
            }
            if (model != null && model.Length > 0)
            {
                sb.Append(" --model ");
                sb.Append(Quote(model));
            }
            for (int i = 0; i < files.Count; i++)
            {
                sb.Append(" --file ");
                sb.Append(Quote(files[i].Path));
            }
            sb.Append(' ');
            sb.Append(Quote(question));
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                    continue;
                }
                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }
                sb.Append(c);
            }
            if (backslashes > 0) sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        private static string JsonValue(string json, string key)
        {
            string pat = "\"" + key + "\":\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return null;
            int p = i + pat.Length;
            StringBuilder sb = new StringBuilder();
            while (p < json.Length)
            {
                char c = json[p];
                if (c == '\\' && p + 1 < json.Length)
                {
                    char n = json[p + 1];
                    p += 2;
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (p + 4 <= json.Length)
                            {
                                int code;
                                bool ok = int.TryParse(json.Substring(p, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out code);
                                if (ok) sb.Append((char)code);
                                p += 4;
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                p++;
            }
            return sb.ToString();
        }

        private static string Shorten(string s, int max)
        {
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        private static string SizeText(long bytes)
        {
            if (bytes < 1024) return bytes.ToString() + " Б";
            if (bytes < 1024L * 1024L) return (bytes / 1024).ToString() + " КБ";
            return (bytes / (1024L * 1024L)).ToString() + " МБ";
        }
    }

    // Небольшой диалог с одной строкой ввода: имя нового проекта.
    internal sealed class PromptForm : Form
    {
        private TextBox box;

        public string Value
        {
            get { return box.Text.Trim(); }
        }

        public PromptForm(string title, string intro, string prompt)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 190);
            Font = new Font("Segoe UI", 10f);

            Label info = new Label();
            info.Text = intro;
            info.Location = new Point(14, 12);
            info.Size = new Size(492, 52);
            info.AccessibleName = "Описание";
            Controls.Add(info);

            Label cap = new Label();
            cap.Text = prompt;
            cap.Location = new Point(14, 70);
            cap.Size = new Size(492, 22);
            Controls.Add(cap);

            box = new TextBox();
            box.Location = new Point(14, 96);
            box.Size = new Size(492, 26);
            box.AccessibleName = prompt;
            box.AccessibleDescription = "Введите название проекта и нажмите ОК.";
            Controls.Add(box);

            Button ok = new Button();
            ok.Text = "&ОК";
            ok.Location = new Point(14, 138);
            ok.Size = new Size(120, 32);
            ok.AccessibleName = "ОК";
            ok.Click += delegate
            {
                if (box.Text.Trim().Length == 0)
                {
                    System.Media.SystemSounds.Beep.Play();
                    box.Focus();
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            Controls.Add(ok);
            AcceptButton = ok;

            Button cancel = new Button();
            cancel.Text = "От&мена";
            cancel.Location = new Point(146, 138);
            cancel.Size = new Size(120, 32);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            CancelButton = cancel;

            Shown += delegate { box.Focus(); };
        }
    }

    internal sealed class ImportItem
    {
        private readonly string label;
        private readonly int count;

        public ImportItem(string label, int count)
        {
            this.label = label == null ? "" : label;
            this.count = count;
        }

        public override string ToString()
        {
            return count > 0
                ? label + " — сообщений: " + count.ToString()
                : label + " — сообщений нет";
        }
    }

    internal sealed class ImportForm : Form
    {
        private ListBox list;
        private int selected = -1;
        private List<int> counts;
        private Label hint;

        public int SelectedIndex
        {
            get { return selected; }
        }

        public ImportForm(List<ModelItem> models, List<int> counts)
        {
            Text = "Импорт истории";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(660, 430);
            Font = new Font("Segoe UI", 10f);

            this.counts = counts;

            Label intro = new Label();
            intro.Text = "Выберите чат другой модели. Его сообщения добавятся в текущий чат, "
                + "и агент учтёт их в следующем ответе. В списке — все другие модели, "
                + "даже если переписки у них пока нет.";
            intro.Location = new Point(14, 12);
            intro.Size = new Size(632, 46);
            intro.AccessibleName = "Описание";
            Controls.Add(intro);

            list = new ListBox();
            list.Location = new Point(14, 64);
            list.Size = new Size(632, 280);
            list.IntegralHeight = false;
            list.AccessibleName = "Чаты других моделей";
            list.AccessibleDescription = "Список моделей и числа сообщений в их чатах. Выберите строку стрелками и нажмите «Импортировать».";
            for (int i = 0; i < models.Count; i++)
            {
                list.Items.Add(new ImportItem(models[i].ToString(), counts[i]));
            }
            if (list.Items.Count > 0)
            {
                int first = 0;
                for (int i = 0; i < counts.Count; i++)
                {
                    if (counts[i] > 0) { first = i; break; }
                }
                list.SelectedIndex = first;
            }
            list.DoubleClick += delegate { accepted(); };
            list.SelectedIndexChanged += delegate
            {
                if (hint == null) return;
                hint.ForeColor = SystemColors.ControlText;
                hint.Text = "Enter — импортировать, Esc — отмена.";
            };
            Controls.Add(list);

            Button ok = new Button();
            ok.Text = "&Импортировать";
            ok.Location = new Point(14, 356);
            ok.Size = new Size(170, 32);
            ok.AccessibleName = "Импортировать историю выбранного чата";
            ok.AccessibleDescription = "Добавляет сообщения выбранного чата в текущий чат";
            ok.Click += delegate { accepted(); };
            Controls.Add(ok);
            AcceptButton = ok;

            Button cancel = new Button();
            cancel.Text = "От&мена";
            cancel.Location = new Point(194, 356);
            cancel.Size = new Size(140, 32);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            CancelButton = cancel;

            hint = new Label();
            hint.Text = "Enter — импортировать, Esc — отмена.";
            hint.Location = new Point(350, 362);
            hint.Size = new Size(296, 22);
            hint.AccessibleName = "Подсказка";
            Controls.Add(hint);
        }

        // Строку без сообщений импортировать нельзя: подсказывает, что не так,
        // и оставляет окно открытым — никакой молчаливой реакции.
        private void accepted()
        {
            int i = list.SelectedIndex;
            if (i < 0 || counts == null || i >= counts.Count) return;
            if (counts[i] <= 0)
            {
                hint.ForeColor = Color.Firebrick;
                hint.Text = "В этом чате нет сообщений. Выберите другой.";
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            selected = i;
            DialogResult = DialogResult.OK;
        }
    }

    internal static class Profile
    {
        private static string filePath;
        private static string cached;
        private static bool loaded;

        public static string FilePath
        {
            get
            {
                if (filePath == null)
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenCodeChat");
                    filePath = Path.Combine(dir, "deepseek.key");
                }
                return filePath;
            }
        }

        public static string Key
        {
            get
            {
                if (!loaded)
                {
                    loaded = true;
                    cached = Read();
                }
                return cached;
            }
        }

        public static bool HasKey
        {
            get { return Key != null && Key.Length > 0; }
        }

        public static string Hint
        {
            get
            {
                string k = Key;
                if (k == null || k.Length == 0) return "ключ не подключён";
                if (k.Length <= 8) return "ключ сохранён";
                return "ключ …" + k.Substring(k.Length - 4);
            }
        }

        public static void Save(string key)
        {
            string dir = Path.GetDirectoryName(FilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            byte[] data = Encoding.UTF8.GetBytes(key);
            byte[] prot = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, prot);
            cached = key;
            loaded = true;
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception ex)
            {
                Log.Exception("Удаление ключа", ex);
            }
            cached = null;
            loaded = true;
        }

        public static void Apply(ProcessStartInfo psi)
        {
            string k = Key;
            if (k != null && k.Length > 0) psi.EnvironmentVariables["DEEPSEEK_API_KEY"] = k;
        }

        private static string Read()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                byte[] prot = File.ReadAllBytes(FilePath);
                byte[] data = ProtectedData.Unprotect(prot, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data).Trim();
            }
            catch (Exception ex)
            {
                Log.Exception("Чтение ключа DeepSeek", ex);
                return null;
            }
        }
    }

    // Другой сервис: Polza.ai, AnyModel или любой адрес, совместимый с OpenAI.
    // Ключ лежит в защищённом файле и передаётся движку переменной окружения.
    internal static class Service
    {
        private static bool loaded;
        private static string key = "";
        private static string baseUrl = "";
        private static string label = "";
        private static string[] models = new string[0];

        private static string Dir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenCodeChat");
            }
        }

        private static string KeyPath { get { return Path.Combine(Dir, "service.key"); } }
        private static string InfoPath { get { return Path.Combine(Dir, "service.txt"); } }

        public static string Key
        {
            get { Load(); return key; }
        }

        public static string BaseUrl
        {
            get { Load(); return baseUrl; }
        }

        public static string Label
        {
            get { Load(); return label.Length > 0 ? label : "Другой сервис"; }
        }

        public static string[] Models
        {
            get { Load(); return models; }
        }

        public static bool HasProfile
        {
            get { return Key.Length > 0 && BaseUrl.Length > 0; }
        }

        public static string Hint
        {
            get
            {
                string k = Key;
                if (k.Length == 0) return "не подключён";
                if (k.Length <= 8) return "ключ сохранён";
                return "ключ …" + k.Substring(k.Length - 4);
            }
        }

        public static void Save(string serviceLabel, string serviceUrl, string serviceKey, string[] serviceModels)
        {
            Load();
            key = serviceKey == null ? "" : serviceKey.Trim();
            baseUrl = serviceUrl == null ? "" : serviceUrl.Trim().TrimEnd('/');
            label = serviceLabel == null ? "" : serviceLabel.Trim();
            models = serviceModels == null ? new string[0] : serviceModels;
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                byte[] data = Encoding.UTF8.GetBytes(key);
                byte[] prot = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(KeyPath, prot);
                List<string> lines = new List<string>();
                lines.Add(label);
                lines.Add(baseUrl);
                for (int i = 0; i < models.Length; i++) lines.Add(models[i]);
                File.WriteAllLines(InfoPath, lines.ToArray(), new UTF8Encoding(false));
                loaded = true;
            }
            catch (Exception ex)
            {
                Log.Exception("Сохранение другого сервиса", ex);
            }
            RememberKey(baseUrl, key);
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(KeyPath)) File.Delete(KeyPath);
                if (File.Exists(InfoPath)) File.Delete(InfoPath);
            }
            catch (Exception ex)
            {
                Log.Exception("Удаление другого сервиса", ex);
            }
            key = "";
            baseUrl = "";
            label = "";
            models = new string[0];
            loaded = true;
        }

        public static void Apply(ProcessStartInfo psi)
        {
            string k = Key;
            if (k.Length > 0) psi.EnvironmentVariables["POLZA_API_KEY"] = k;
        }

        // Ключи обоих известных сервисов хранятся по отдельности: активным
        // может быть только один, а баланс нужно уметь проверять у обоих.
        private static string ServicesDir { get { return Path.Combine(Dir, "services"); } }

        public static string ProviderOf(string url)
        {
            if (url == null) return "";
            string u = url.ToLowerInvariant();
            if (u.Contains("polza")) return "polza";
            if (u.Contains("anymodel")) return "anymodel";
            return "";
        }

        private static void RememberKey(string url, string value)
        {
            string provider = ProviderOf(url);
            if (provider.Length == 0 || value == null || value.Length == 0) return;
            try
            {
                if (!Directory.Exists(ServicesDir)) Directory.CreateDirectory(ServicesDir);
                byte[] data = Encoding.UTF8.GetBytes(value);
                byte[] prot = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(Path.Combine(ServicesDir, provider + ".key"), prot);
            }
            catch (Exception ex)
            {
                Log.Exception("Сохранение ключа сервиса", ex);
            }
        }

        // Ключ провайдера: сохранённый ранее, иначе — активный ключ,
        // если это тот же провайдер.
        public static string KeyFor(string provider)
        {
            if (provider == null || provider.Length == 0) return "";
            try
            {
                string path = Path.Combine(ServicesDir, provider + ".key");
                if (File.Exists(path))
                {
                    byte[] prot = File.ReadAllBytes(path);
                    byte[] data = ProtectedData.Unprotect(prot, null, DataProtectionScope.CurrentUser);
                    string k = Encoding.UTF8.GetString(data).Trim();
                    if (k.Length > 0) return k;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("Чтение ключа сервиса", ex);
            }
            string active = ProviderOf(BaseUrl);
            if (string.Equals(active, provider, StringComparison.OrdinalIgnoreCase)) return Key;
            return "";
        }

        // Проверка ключа настоящим запросом к модели. Список моделей у многих
        // сервисов открыт и без ключа: 200 на /models ничего не доказывает,
        // ключ отсекается только на /chat/completions. Возвращает пустую
        // строку, если ключ принят, иначе — причину простыми словами.
        public static string ProbeKey(string url, string apiKey, string model)
        {
            if (url == null || url.Length == 0 || apiKey == null || apiKey.Length == 0)
                return "";
            if (model == null || model.Length == 0) model = "test";
            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol
                    | SecurityProtocolType.Tls12;
            }
            catch
            {
            }
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url.TrimEnd('/') + "/chat/completions");
                req.Proxy = null;
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 30000;
                req.ReadWriteTimeout = 30000;
                req.KeepAlive = false;
                req.UserAgent = "SlonobassAgent";
                req.Headers["Authorization"] = "Bearer " + apiKey;
                string body = "{\"model\":" + Quote(model)
                    + ",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}";
                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    int code = (int)resp.StatusCode;
                    return code >= 200 && code < 300 ? "" : "сервис ответил кодом " + code.ToString();
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp == null) return "нет связи с сервисом: " + ex.Message;
                int code = (int)resp.StatusCode;
                if (code == 401 || code == 403)
                    return "сервис отклонил ключ (код " + code.ToString() + ")";
                if (code == 402)
                    return "на балансе сервиса нет средств (код 402)";
                string detail = "";
                try
                {
                    using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        detail = r.ReadToEnd();
                }
                catch
                {
                }
                detail = detail.Trim();
                if (detail.Length > 200) detail = detail.Substring(0, 200) + "…";
                return "сервис ответил кодом " + code.ToString()
                    + (detail.Length > 0 ? ": " + detail : "");
            }
            catch (Exception ex)
            {
                return "не удалось проверить ключ: " + ex.Message;
            }
        }

        private static string Quote(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (File.Exists(KeyPath))
                {
                    byte[] prot = File.ReadAllBytes(KeyPath);
                    byte[] data = ProtectedData.Unprotect(prot, null, DataProtectionScope.CurrentUser);
                    key = Encoding.UTF8.GetString(data).Trim();
                }
                if (File.Exists(InfoPath))
                {
                    string[] lines = File.ReadAllLines(InfoPath, Encoding.UTF8);
                    if (lines.Length > 0) label = lines[0].Trim();
                    if (lines.Length > 1) baseUrl = lines[1].Trim();
                    List<string> list = new List<string>();
                    for (int i = 2; i < lines.Length; i++)
                    {
                        string s = lines[i].Trim();
                        if (s.Length > 0) list.Add(s);
                    }
                    models = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log.Exception("Чтение другого сервиса", ex);
            }
        }
    }

    // Баланс сервисов: Polza.ai отдаёт сумму в рублях, AnyModel — остаток
    // токенов. Оба запроса идут по сохранённым ключам провайдеров.
    internal static class BalanceInfo
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public static string Polza(string apiKey)
        {
            if (apiKey == null || apiKey.Length == 0)
                return "Polza.ai: ключ не сохранён. Введите его в «Другие сервисы…» — ключ запомнится.";
            string body, err = "";
            bool ok = Get("https://polza.ai/api/v2/balance", apiKey, out body, out err);
            string text = ok ? FormatPolza(body) : "";
            if (text.Length > 0) return "Polza.ai: " + text;
            string err2 = "";
            ok = Get("https://polza.ai/api/v1/balance", apiKey, out body, out err2);
            text = ok ? FormatPolza(body) : "";
            if (text.Length > 0) return "Polza.ai: " + text;
            string reason = err.Length > 0 ? err : err2;
            return "Polza.ai: не удалось узнать баланс — "
                + (reason.Length > 0 ? reason : "сервис не прислал сумму.");
        }

        public static string Anymodel(string apiKey)
        {
            if (apiKey == null || apiKey.Length == 0)
                return "AnyModel: ключ не сохранён. Введите его в «Другие сервисы…» — ключ запомнится.";
            string body, err;
            if (!Get("https://anymodel.org/v1/balance", apiKey, out body, out err))
                return "AnyModel: не удалось узнать баланс — " + err;
            string unlimited = Text(body, "unlimited");
            if (string.Equals(unlimited, "true", StringComparison.OrdinalIgnoreCase))
                return "AnyModel: без ограничений.";
            double balance = Num(body, "balance");
            if (double.IsNaN(balance))
                return "AnyModel: не удалось узнать баланс — сервис не прислал остаток.";
            string res = "AnyModel: " + balance.ToString("N0", Ru) + " токенов";
            string status = Text(body, "status");
            if (status.Length > 0 && !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
                res += " (статус ключа: " + status + ")";
            return res;
        }

        private static string FormatPolza(string body)
        {
            double available = Num(body, "available");
            double amount = Num(body, "amount");
            double spent = Num(body, "spentAmount");
            if (double.IsNaN(available) && double.IsNaN(amount)) return "";
            StringBuilder sb = new StringBuilder();
            if (!double.IsNaN(available))
            {
                sb.Append("доступно ").Append(Money(available));
                if (!double.IsNaN(amount)) sb.Append(" (на счёте ").Append(Money(amount));
                if (!double.IsNaN(spent)) sb.Append(", потрачено ").Append(Money(spent));
                if (!double.IsNaN(amount)) sb.Append(")");
            }
            else
            {
                sb.Append("на счёте ").Append(Money(amount));
                if (!double.IsNaN(spent)) sb.Append(", потрачено ").Append(Money(spent));
            }
            return sb.ToString();
        }

        private static string Money(double value)
        {
            return value.ToString("N2", Ru) + " ₽";
        }

        private static bool Get(string url, string apiKey, out string body, out string err)
        {
            body = "";
            err = "";
            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol
                    | SecurityProtocolType.Tls12;
            }
            catch
            {
            }
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Proxy = null;
                req.Method = "GET";
                req.Accept = "application/json";
                req.Timeout = 20000;
                req.ReadWriteTimeout = 20000;
                req.KeepAlive = false;
                req.UserAgent = "SlonobassAgent";
                req.Headers["Authorization"] = "Bearer " + apiKey;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    body = r.ReadToEnd();
                    return true;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp == null)
                {
                    err = "нет связи с сервисом: " + ex.Message;
                    return false;
                }
                int code = (int)resp.StatusCode;
                if (code == 401 || code == 403) err = "ключ отклонён (код " + code.ToString() + ")";
                else if (code == 402) err = "доступ к балансу закрыт (код 402)";
                else if (code == 404) err = "адрес баланса не найден (код 404)";
                else err = "сервис ответил кодом " + code.ToString();
                return false;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        private static double Num(string json, string field)
        {
            if (json == null || json.Length == 0) return double.NaN;
            Match m = Regex.Match(json, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"?(-?[0-9]+(?:\\.[0-9]+)?)");
            if (!m.Success) return double.NaN;
            double v;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return double.NaN;
            return v;
        }

        private static string Text(string json, string field)
        {
            if (json == null || json.Length == 0) return "";
            Match m = Regex.Match(json, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"?([^\",}]*)");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
    }

    internal sealed class ProfileForm : Form
    {
        private TextBox keyBox;
        private CheckBox showBox;
        private Label stateLabel;
        private Button deleteBtn;

        public ProfileForm()
        {
            Text = "Мой профиль — DeepSeek";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(640, 372);
            Font = new Font("Segoe UI", 10f);

            Label intro = new Label();
            intro.Text = "Свой аккаунт DeepSeek.\r\n\r\n"
                + "Если вставить сюда ключ, сверху в списке моделей появится раздел «Мой профиль» "
                + "с моделями DeepSeek. Запросы пойдут на ваш аккаунт — на ваши оплаченные токены.\r\n\r\n"
                + "Ключ создаётся на сайте platform.deepseek.com в разделе API keys.";
            intro.Location = new Point(14, 12);
            intro.Size = new Size(612, 118);
            intro.AccessibleName = "Описание";
            Controls.Add(intro);

            stateLabel = new Label();
            stateLabel.Location = new Point(14, 134);
            stateLabel.Size = new Size(612, 24);
            stateLabel.AccessibleName = "Состояние профиля";
            Controls.Add(stateLabel);
            UpdateState();

            Label keyLabel = new Label();
            keyLabel.Text = "&Ключ DeepSeek (начинается с sk-):";
            keyLabel.Location = new Point(14, 162);
            keyLabel.Size = new Size(320, 22);
            Controls.Add(keyLabel);

            keyBox = new TextBox();
            keyBox.Location = new Point(14, 186);
            keyBox.Width = 612;
            keyBox.UseSystemPasswordChar = true;
            keyBox.AccessibleName = "Ключ DeepSeek";
            keyBox.AccessibleDescription = "Ключ DeepSeek";
            keyBox.Text = Profile.Key != null ? Profile.Key : "";
            Controls.Add(keyBox);

            showBox = new CheckBox();
            showBox.Text = "По&казать ключ";
            showBox.Location = new Point(14, 216);
            showBox.Size = new Size(180, 24);
            showBox.CheckedChanged += delegate { keyBox.UseSystemPasswordChar = !showBox.Checked; };
            Controls.Add(showBox);

            LinkLabel link = new LinkLabel();
            link.Text = "&Страница ключей DeepSeek в браузере";
            link.Location = new Point(200, 216);
            link.Size = new Size(420, 24);
            link.AccessibleName = "Открыть страницу ключей DeepSeek в браузере";
            link.LinkClicked += delegate
            {
                try
                {
                    Process.Start("https://platform.deepseek.com/api_keys");
                }
                catch (Exception ex)
                {
                    Log.Exception("Открыть сайт DeepSeek", ex);
                }
            };
            Controls.Add(link);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 52;

            Button saveBtn = new Button();
            saveBtn.Text = "&Сохранить";
            saveBtn.Width = 130;
            saveBtn.Location = new Point(14, 12);
            saveBtn.AccessibleName = "Сохранить ключ";
            saveBtn.Click += delegate { SaveKey(); };
            bottom.Controls.Add(saveBtn);

            deleteBtn = new Button();
            deleteBtn.Text = "&Удалить ключ";
            deleteBtn.Width = 130;
            deleteBtn.Location = new Point(154, 12);
            deleteBtn.AccessibleName = "Удалить ключ DeepSeek";
            deleteBtn.Enabled = Profile.HasKey;
            deleteBtn.Click += delegate { DeleteKey(); };
            bottom.Controls.Add(deleteBtn);

            Button cancelBtn = new Button();
            cancelBtn.Text = "Отмена";
            cancelBtn.Width = 110;
            cancelBtn.Location = new Point(294, 12);
            cancelBtn.DialogResult = DialogResult.Cancel;
            bottom.Controls.Add(cancelBtn);

            Controls.Add(bottom);
            AcceptButton = saveBtn;
            CancelButton = cancelBtn;
        }

        private void UpdateState()
        {
            stateLabel.Text = "Сейчас: " + Profile.Hint + ".";
        }

        private void SaveKey()
        {
            string k = keyBox.Text.Trim();
            if (k.Length == 0)
            {
                MessageBox.Show(this,
                    "Введите ключ DeepSeek. Чтобы отключить профиль, нажмите «Удалить ключ».",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                keyBox.Focus();
                return;
            }
            if (!k.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
            {
                DialogResult r = MessageBox.Show(this,
                    "Обычно ключ DeepSeek начинается с sk-. Этот не похож. Сохранить всё равно?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    keyBox.Focus();
                    return;
                }
            }
            try
            {
                Profile.Save(k);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                Log.Exception("Сохранение ключа DeepSeek", ex);
                MessageBox.Show(this, "Не удалось сохранить ключ: " + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DeleteKey()
        {
            DialogResult r = MessageBox.Show(this,
                "Удалить сохранённый ключ DeepSeek? Тогда останутся только бесплатные модели.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            Profile.Clear();
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    internal sealed class ServiceForm : Form
    {
        private static readonly string[] PresetNames = new string[]
        {
            "Polza.ai — polza.ai/api/v1",
            "AnyModel — anymodel.org/v1",
            "Свой адрес…"
        };
        private static readonly string[] PresetUrls = new string[]
        {
            "https://polza.ai/api/v1",
            "https://anymodel.org/v1",
            ""
        };
        private static readonly string[] PresetLabels = new string[]
        {
            "Polza.ai",
            "AnyModel",
            ""
        };

        private ComboBox presetBox;
        private TextBox baseBox;
        private TextBox keyBox;
        private CheckBox showBox;
        private CheckedListBox modelsList;
        private Label stateLabel;
        private Label fetchLabel;
        private Button checkBtn;
        private Button saveBtn;

        public ServiceForm()
        {
            Text = "Другие сервисы";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(760, 690);
            Font = new Font("Segoe UI", 10f);

            Label intro = new Label();
            intro.Text = "Polza.ai, AnyModel или любой сервис с адресом в стиле OpenAI.\r\n\r\n"
                + "Положите сюда ключ и отметьте нужные модели — они появятся сверху в списке "
                + "моделей отдельным разделом. Запросы пойдут на ваш аккаунт в этом сервисе, "
                + "на ваши оплаченные токены.\r\n\r\n"
                + "Ключ хранится только на этом компьютере и защищён учётной записью Windows.";
            intro.Location = new Point(14, 10);
            intro.Size = new Size(732, 96);
            intro.AccessibleName = "Описание";
            Controls.Add(intro);

            stateLabel = new Label();
            stateLabel.Location = new Point(14, 108);
            stateLabel.Size = new Size(732, 24);
            stateLabel.AccessibleName = "Состояние сервиса";
            Controls.Add(stateLabel);

            Label presetLabel = new Label();
            presetLabel.Text = "&Сервис:";
            presetLabel.Location = new Point(14, 140);
            presetLabel.Size = new Size(200, 22);
            Controls.Add(presetLabel);

            presetBox = new ComboBox();
            presetBox.DropDownStyle = ComboBoxStyle.DropDownList;
            presetBox.Location = new Point(14, 162);
            presetBox.Width = 420;
            presetBox.AccessibleName = "Сервис";
            presetBox.AccessibleDescription = "Polza.ai, AnyModel или свой адрес";
            for (int i = 0; i < PresetNames.Length; i++) presetBox.Items.Add(PresetNames[i]);
            presetBox.SelectedIndexChanged += delegate { ApplyPreset(); };
            Controls.Add(presetBox);

            Label baseLabel = new Label();
            baseLabel.Text = "&Адрес API:";
            baseLabel.Location = new Point(14, 196);
            baseLabel.Size = new Size(200, 22);
            Controls.Add(baseLabel);

            baseBox = new TextBox();
            baseBox.Location = new Point(14, 218);
            baseBox.Width = 732;
            baseBox.AccessibleName = "Адрес API";
            baseBox.AccessibleDescription = "Например https://polza.ai/api/v1";
            Controls.Add(baseBox);

            Label keyLabel = new Label();
            keyLabel.Text = "&Ключ сервиса:";
            keyLabel.Location = new Point(14, 252);
            keyLabel.Size = new Size(300, 22);
            Controls.Add(keyLabel);

            keyBox = new TextBox();
            keyBox.Location = new Point(14, 274);
            keyBox.Width = 540;
            keyBox.UseSystemPasswordChar = true;
            keyBox.AccessibleName = "Ключ сервиса";
            keyBox.AccessibleDescription = "Ключ Polza.ai начинается с pza_";
            Controls.Add(keyBox);

            showBox = new CheckBox();
            showBox.Text = "По&казать ключ";
            showBox.Location = new Point(566, 274);
            showBox.Size = new Size(180, 24);
            showBox.CheckedChanged += delegate { keyBox.UseSystemPasswordChar = !showBox.Checked; };
            Controls.Add(showBox);

            checkBtn = new Button();
            checkBtn.Text = "&Проверить и получить модели";
            checkBtn.Location = new Point(14, 310);
            checkBtn.Size = new Size(260, 30);
            checkBtn.AccessibleName = "Проверить ключ и получить список моделей";
            checkBtn.Click += delegate { CheckModels(); };
            Controls.Add(checkBtn);

            Label modelsLabel = new Label();
            modelsLabel.Text = "Моде&ли сервиса — отметьте нужные (пробел):";
            modelsLabel.Location = new Point(14, 348);
            modelsLabel.Size = new Size(600, 22);
            Controls.Add(modelsLabel);

            modelsList = new CheckedListBox();
            modelsList.Location = new Point(14, 370);
            modelsList.Size = new Size(732, 170);
            modelsList.CheckOnClick = true;
            modelsList.AccessibleName = "Модели сервиса";
            Controls.Add(modelsList);

            Button clearBtn = new Button();
            clearBtn.Text = "&Снять всё";
            clearBtn.Location = new Point(14, 548);
            clearBtn.Size = new Size(150, 30);
            clearBtn.AccessibleName = "Снять все галочки с моделей";
            clearBtn.AccessibleDescription = "Снимает отметки со всех моделей в списке";
            clearBtn.Click += delegate { SetAllChecked(false); };
            Controls.Add(clearBtn);

            Button allBtn = new Button();
            allBtn.Text = "Вы&делить всё";
            allBtn.Location = new Point(174, 548);
            allBtn.Size = new Size(150, 30);
            allBtn.AccessibleName = "Выделить все модели";
            allBtn.AccessibleDescription = "Отмечает все модели в списке";
            allBtn.Click += delegate { SetAllChecked(true); };
            Controls.Add(allBtn);

            fetchLabel = new Label();
            fetchLabel.Location = new Point(14, 584);
            fetchLabel.Size = new Size(732, 22);
            fetchLabel.AccessibleName = "Результат проверки";
            fetchLabel.Name = "fetchLabel";
            Controls.Add(fetchLabel);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 52;

            saveBtn = new Button();
            saveBtn.Text = "&Сохранить";
            saveBtn.Width = 130;
            saveBtn.Location = new Point(14, 12);
            saveBtn.AccessibleName = "Сохранить сервис";
            saveBtn.Click += delegate { SaveProfile(); };
            bottom.Controls.Add(saveBtn);

            Button deleteBtn = new Button();
            deleteBtn.Text = "&Удалить сервис";
            deleteBtn.Width = 130;
            deleteBtn.Location = new Point(154, 12);
            deleteBtn.AccessibleName = "Удалить другой сервис";
            deleteBtn.Enabled = Service.HasProfile;
            deleteBtn.Click += delegate { DeleteProfile(); };
            bottom.Controls.Add(deleteBtn);

            Button cancelBtn = new Button();
            cancelBtn.Text = "Отмена";
            cancelBtn.Width = 110;
            cancelBtn.Location = new Point(294, 12);
            cancelBtn.DialogResult = DialogResult.Cancel;
            bottom.Controls.Add(cancelBtn);

            Controls.Add(bottom);
            AcceptButton = saveBtn;
            CancelButton = cancelBtn;

            LoadExisting();
        }

        private void LoadExisting()
        {
            string url = Service.BaseUrl;
            int preset = PresetUrls.Length - 1;
            for (int i = 0; i < PresetUrls.Length - 1; i++)
            {
                if (PresetUrls[i].Length > 0 && string.Equals(url, PresetUrls[i], StringComparison.OrdinalIgnoreCase))
                {
                    preset = i;
                    break;
                }
            }
            presetBox.SelectedIndex = preset;
            if (url.Length > 0) baseBox.Text = url;
            else baseBox.Text = PresetUrls[0];
            baseBox.ReadOnly = preset != PresetUrls.Length - 1;
            keyBox.Text = Service.Key;
            string[] saved = Service.Models;
            modelsList.Items.Clear();
            for (int i = 0; i < saved.Length; i++) modelsList.Items.Add(saved[i], true);
            if (fetchLabel != null && modelsList.Items.Count > 0) fetchLabel.Text = CheckedInfo();
            UpdateState();
        }

        private void UpdateState()
        {
            if (Service.HasProfile)
                stateLabel.Text = "Сейчас: " + Service.Label + ", " + Service.Hint
                    + ", моделей: " + Service.Models.Length.ToString() + ".";
            else
                stateLabel.Text = "Сейчас: другой сервис не подключён.";
        }

        private void ApplyPreset()
        {
            int i = presetBox.SelectedIndex;
            if (i < 0) return;
            bool own = i == PresetUrls.Length - 1;
            baseBox.ReadOnly = !own;
            if (!own) baseBox.Text = PresetUrls[i];
            keyBox.AccessibleDescription = i == 0 ? "Ключ Polza.ai начинается с pza_" : "Ключ сервиса";
        }

        private string CurrentLabel()
        {
            int i = presetBox.SelectedIndex;
            if (i >= 0 && i < PresetLabels.Length && PresetLabels[i].Length > 0) return PresetLabels[i];
            string url = baseBox.Text.Trim();
            try { return new Uri(url).Host; }
            catch { return "Другой сервис"; }
        }

        private void CheckModels()
        {
            string url = baseBox.Text.Trim().TrimEnd('/');
            string k = keyBox.Text.Trim();
            if (k.Length == 0)
            {
                MessageBox.Show(this, "Введите ключ сервиса.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                keyBox.Focus();
                return;
            }
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Адрес API должен начинаться с http:// или https://.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                baseBox.Focus();
                return;
            }
            stateLabel.Text = "Проверяю ключ и получаю список моделей…";
            checkBtn.Enabled = false;
            saveBtn.Enabled = false;
            Thread t = new Thread(delegate()
            {
                List<string> found = new List<string>();
                string err = null;
                try
                {
                    string body = FetchModels(url + "/models", k);
                    MatchCollection ms = Regex.Matches(body, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                    for (int i = 0; i < ms.Count; i++)
                    {
                        string id = ms[i].Groups[1].Value.Trim();
                        if (id.Length == 0) continue;
                        string low = id.ToLowerInvariant();
                        if (low.IndexOf("embed") >= 0) continue;
                        if (low.IndexOf("whisper") >= 0) continue;
                        if (low.IndexOf("rerank") >= 0) continue;
                        if (low.IndexOf("moderation") >= 0) continue;
                        if (!found.Contains(id)) found.Add(id);
                    }
                }
                catch (Exception ex)
                {
                    err = Describe(ex);
                }
                string probe = null;
                if (err == null && found.Count > 0)
                {
                    // Список моделей открыт без ключа, поэтому ключ проверяем
                    // отдельным коротким запросом к выбранной модели.
                    probe = Service.ProbeKey(url, k, found[0]);
                }
                try
                {
                    BeginInvoke(new Action(delegate { AfterCheck(found, err, probe); }));
                }
                catch
                {
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void AfterCheck(List<string> found, string err, string probe)
        {
            checkBtn.Enabled = true;
            saveBtn.Enabled = true;
            if (err != null)
            {
                stateLabel.Text = "Проверка не удалась: " + err;
                if (fetchLabel != null) fetchLabel.Text = "Ключ не проверен. Можно сохранить и попробовать позже.";
                return;
            }
            if (found.Count == 0)
            {
                stateLabel.Text = "Сервис ответил, но моделей в ответе нет.";
                if (fetchLabel != null) fetchLabel.Text = "Проверьте адрес API: список моделей берётся с адреса …/models.";
                return;
            }
            List<string> previous = new List<string>(Service.Models);
            modelsList.Items.Clear();
            for (int i = 0; i < found.Count; i++)
            {
                // Новые модели не отмечаем сами: иначе в главном списке
                // окажутся все модели сервиса. Отмечается только то, что
                // человек выбрал раньше или выберет сейчас.
                modelsList.Items.Add(found[i], previous.Contains(found[i]));
            }
            if (fetchLabel != null) fetchLabel.Text = CheckedInfo() + " Нажмите «Снять всё», отметьте нужные и «Сохранить».";
            if (probe != null && probe.Length > 0)
            {
                stateLabel.Text = "Модели получены (" + found.Count.ToString()
                    + "), но ключ не работает: " + probe + ".";
                if (fetchLabel != null) fetchLabel.Text = probe.IndexOf("отклонил ключ", StringComparison.Ordinal) >= 0
                    ? "Список моделей у сервиса открыт без ключа, поэтому модели видны и с неверным ключом. Создайте новый ключ на сайте сервиса и введите его здесь."
                    : "Можно сохранить, но запросы могут не проходить.";
                return;
            }
            stateLabel.Text = "Ключ принят. Найдено моделей: " + found.Count.ToString() + ".";
        }

        // «Снять всё» и «Выделить всё». Считаем отметки выше и сообщаем
        // результат словами — так человек знает, что нажатие сработало.
        private void SetAllChecked(bool value)
        {
            if (modelsList.Items.Count == 0)
            {
                if (fetchLabel != null) fetchLabel.Text = "Список моделей пуст. Сначала нажмите «Проверить и получить модели».";
                modelsList.Focus();
                return;
            }
            for (int i = 0; i < modelsList.Items.Count; i++) modelsList.SetItemChecked(i, value);
            if (modelsList.Items.Count > 0) modelsList.SelectedIndex = 0;
            if (fetchLabel != null) fetchLabel.Text = CheckedInfo() + " Не забудьте «Сохранить».";
            modelsList.Focus();
        }

        private string CheckedInfo()
        {
            int n = 0;
            for (int i = 0; i < modelsList.Items.Count; i++)
            {
                if (modelsList.GetItemChecked(i)) n++;
            }
            return "Отмечено моделей: " + n.ToString() + " из " + modelsList.Items.Count.ToString() + ".";
        }

        private static string Describe(Exception ex)
        {
            WebException we = ex as WebException;
            if (we != null)
            {
                HttpWebResponse resp = we.Response as HttpWebResponse;
                if (resp != null)
                {
                    string body = "";
                    try
                    {
                        using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                            body = r.ReadToEnd();
                    }
                    catch { }
                    body = body.Trim();
                    if (body.Length > 300) body = body.Substring(0, 300) + "…";
                    string code = ((int)resp.StatusCode).ToString() + " " + resp.StatusDescription;
                    return body.Length > 0 ? code + ": " + body : code;
                }
                return we.Message;
            }
            return ex.Message;
        }

        private static string FetchModels(string url, string key)
        {
            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol
                    | SecurityProtocolType.Tls12;
            }
            catch
            {
            }
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null;
            req.Method = "GET";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            req.KeepAlive = false;
            req.UserAgent = "SlonobassAgent";
            req.Accept = "application/json";
            req.Headers["Authorization"] = "Bearer " + key;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return r.ReadToEnd();
            }
        }

        private void SaveProfile()
        {
            string url = baseBox.Text.Trim().TrimEnd('/');
            string k = keyBox.Text.Trim();
            if (k.Length == 0)
            {
                MessageBox.Show(this, "Введите ключ сервиса. Чтобы отключить сервис, нажмите «Удалить сервис».",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                keyBox.Focus();
                return;
            }
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Адрес API должен начинаться с http:// или https://.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                baseBox.Focus();
                return;
            }
            List<string> selected = new List<string>();
            for (int i = 0; i < modelsList.Items.Count; i++)
            {
                if (modelsList.GetItemChecked(i)) selected.Add(modelsList.Items[i].ToString());
            }
            if (selected.Count == 0)
            {
                MessageBox.Show(this,
                    "Не отмечено ни одной модели.\r\n"
                    + "Нажмите «Проверить и получить модели», отметьте нужные пробелом и сохраните.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                checkBtn.Focus();
                return;
            }
            Service.Save(CurrentLabel(), url, k, selected.ToArray());
            Log.Write("SERVICE", "Сохранён сервис " + CurrentLabel() + " (" + url + "), моделей: "
                + selected.Count.ToString());
            DialogResult = DialogResult.OK;
            Close();
        }

        private void DeleteProfile()
        {
            DialogResult r = MessageBox.Show(this,
                "Удалить ключ и список моделей другого сервиса?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            Service.Clear();
            Log.Write("SERVICE", "Другой сервис удалён");
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    internal static class Log
    {
        private static readonly object gate = new object();
        private static string folder;
        private static string current;

        public static string Folder
        {
            get { return folder; }
        }

        public static void Init(string baseDir)
        {
            try
            {
                folder = Path.Combine(baseDir, "log");
                Directory.CreateDirectory(folder);
                Open(0);
            }
            catch
            {
                try
                {
                    folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "OpenCodeChat", "log");
                    Directory.CreateDirectory(folder);
                    Open(0);
                }
                catch
                {
                    folder = null;
                    current = null;
                }
            }
            Write("START", "Программа запущена. Версия " + ChatForm.AppVersion.ToString()
                + ". Папка программы: " + baseDir);
            Write("START", "Windows: " + Environment.OSVersion.VersionString
                + "; .NET: " + Environment.Version.ToString()
                + "; 64-bit: " + Environment.Is64BitProcess.ToString());
        }

        private static void Open(int index)
        {
            string name = "chat-" + DateTime.Now.ToString("yyyy-MM-dd");
            if (index > 0) name = name + "-" + index.ToString();
            string path = Path.Combine(folder, name + ".log");
            if (File.Exists(path) && new FileInfo(path).Length > 8L * 1024L * 1024L)
            {
                Open(index + 1);
                return;
            }
            current = path;
        }

        public static void Write(string category, string text)
        {
            string target = current;
            if (target == null) return;
            StringBuilder sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.Append(" [");
            sb.Append(category);
            sb.Append("] ");
            if (text != null) sb.Append(text);
            sb.Append("\r\n");
            lock (gate)
            {
                try
                {
                    File.AppendAllText(target, sb.ToString(), Encoding.UTF8);
                }
                catch
                {
                }
            }
        }

        public static void Exception(string where, Exception ex)
        {
            Write("ERROR", where + ": " + (ex == null ? "(нет описания)" : ex.ToString()));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    // Чтение метаданных GGUF: обученный контекст и размер KV-кэша на токен.
    internal static class Gguf
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        public static long TotalRamBytes()
        {
            try
            {
                MemoryStatusEx m = new MemoryStatusEx();
                m.dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                if (GlobalMemoryStatusEx(ref m)) return (long)m.ullTotalPhys;
            }
            catch
            {
            }
            return 8L * 1024 * 1024 * 1024;
        }

        private static bool IsIntType(uint t)
        {
            return t == 0 || t == 1 || t == 2 || t == 3 || t == 4 || t == 5 || t == 10 || t == 11;
        }

        private static uint ReadUint(BinaryReader br, uint type)
        {
            switch (type)
            {
                case 0: return br.ReadByte();
                case 1: return (uint)(byte)br.ReadSByte();
                case 2: return br.ReadUInt16();
                case 3: return (uint)(ushort)br.ReadInt16();
                case 4: return br.ReadUInt32();
                case 5: return (uint)br.ReadInt32();
                case 10: { ulong v = br.ReadUInt64(); return v > uint.MaxValue ? uint.MaxValue : (uint)v; }
                case 11: { long v2 = br.ReadInt64(); return v2 < 0 ? 0 : (v2 > uint.MaxValue ? uint.MaxValue : (uint)v2); }
                default: throw new InvalidDataException("не целое");
            }
        }

        private static void Skip(BinaryReader br, uint type)
        {
            switch (type)
            {
                case 0: case 1: case 7: br.ReadByte(); return;
                case 2: case 3: br.ReadUInt16(); return;
                case 4: case 5: case 6: br.ReadUInt32(); return;
                case 10: case 11: case 12: br.ReadUInt64(); return;
                case 8:
                    {
                        ulong len = br.ReadUInt64();
                        if (len > 16UL * 1024UL * 1024UL) throw new InvalidDataException("строка");
                        br.BaseStream.Seek((long)len, SeekOrigin.Current);
                        return;
                    }
                case 9:
                    {
                        uint et = br.ReadUInt32();
                        ulong n = br.ReadUInt64();
                        if (n > 100000000UL) throw new InvalidDataException("массив");
                        for (ulong i = 0; i < n; i++) Skip(br, et);
                        return;
                    }
                default: throw new InvalidDataException("тип " + type.ToString());
            }
        }

        private static string ReadString(BinaryReader br)
        {
            ulong len = br.ReadUInt64();
            if (len > 1024UL * 1024UL) throw new InvalidDataException("ключ");
            byte[] b = br.ReadBytes((int)len);
            if (b.Length != (int)len) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(b);
        }

        public static bool TryRead(string path, out int trainContext, out long kvBytesPerToken)
        {
            trainContext = 0;
            kvBytesPerToken = 0;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (br.ReadUInt32() != 0x46554747) return false;
                    uint version = br.ReadUInt32();
                    if (version < 2 || version > 3) return false;
                    br.ReadUInt64();
                    ulong count = br.ReadUInt64();
                    if (count == 0 || count > 1000000UL) return false;

                    uint train = 0, blocks = 0, heads = 0, embed = 0, keyLen = 0;
                    uint[] kvHeads = null;

                    for (ulong i = 0; i < count; i++)
                    {
                        if (train > 0 && blocks > 0 && kvHeads != null && (keyLen > 0 || embed > 0)) break;
                        string key = ReadString(br);
                        uint type = br.ReadUInt32();
                        bool isInt = IsIntType(type);
                        if (isInt && key.EndsWith(".context_length", StringComparison.Ordinal)) { train = ReadUint(br, type); continue; }
                        if (isInt && key.EndsWith(".block_count", StringComparison.Ordinal)) { blocks = ReadUint(br, type); continue; }
                        if (isInt && key.EndsWith(".attention.head_count", StringComparison.Ordinal)) { heads = ReadUint(br, type); continue; }
                        if (isInt && key.EndsWith(".attention.key_length", StringComparison.Ordinal)) { keyLen = ReadUint(br, type); continue; }
                        if (isInt && key.EndsWith(".embedding_length", StringComparison.Ordinal)) { embed = ReadUint(br, type); continue; }
                        if (key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal))
                        {
                            if (isInt)
                            {
                                kvHeads = new uint[] { ReadUint(br, type) };
                                continue;
                            }
                            if (type == 9)
                            {
                                uint et = br.ReadUInt32();
                                ulong n = br.ReadUInt64();
                                if (IsIntType(et) && n > 0 && n < 4096)
                                {
                                    kvHeads = new uint[(int)n];
                                    for (ulong k = 0; k < n; k++) kvHeads[k] = ReadUint(br, et);
                                    continue;
                                }
                                if (n > 100000000UL) return false;
                                for (ulong k = 0; k < n; k++) Skip(br, et);
                                continue;
                            }
                        }
                        Skip(br, type);
                    }

                    if (train == 0 || blocks == 0) return false;
                    if (heads == 0 && embed > 0 && keyLen > 0) heads = embed / keyLen;
                    long kvTotal = 0;
                    if (kvHeads != null)
                    {
                        for (int i = 0; i < kvHeads.Length; i++) kvTotal += kvHeads[i];
                    }
                    else
                    {
                        kvTotal = heads;
                    }
                    long headDim = keyLen;
                    if (headDim == 0 && heads > 0 && embed > 0) headDim = embed / heads;
                    if (kvTotal <= 0 || headDim <= 0) return false;
                    trainContext = (int)Math.Min(train, 1048576u);
                    // K и V, по 2 байта (fp16) на слой, голову и измерение.
                    kvBytesPerToken = 2L * blocks * kvTotal * headDim * 2L;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    // Мягкие звуки интерфейса: приходящий ответ, отправка, сохранение.
    internal static class Sounds
    {
        [DllImport("winmm.dll")]
        private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);

        private const uint SndAsync = 0x0001;
        private const uint SndNoDefault = 0x0002;
        private const uint SndMemory = 0x0004;

        private static readonly byte[] incoming;
        private static readonly byte[] outgoing;
        private static readonly byte[] saved;

        static Sounds()
        {
            int rate = 22050;
            List<short> up = new List<short>();
            Tone(up, rate, 587.0, 0.11, 0.16, 0.02);
            Tone(up, rate, 880.0, 0.16, 0.13, 0.0);
            incoming = Wav(up, rate);
            List<short> down = new List<short>();
            Tone(down, rate, 523.0, 0.09, 0.11, 0.0);
            outgoing = Wav(down, rate);
            List<short> ok = new List<short>();
            Tone(ok, rate, 784.0, 0.08, 0.11, 0.0);
            Tone(ok, rate, 1046.0, 0.10, 0.09, 0.0);
            saved = Wav(ok, rate);
        }

        public static void Incoming() { Play(incoming); }
        public static void Outgoing() { Play(outgoing); }
        public static void Saved() { Play(saved); }

        private static void Play(byte[] wav)
        {
            try { PlaySound(wav, IntPtr.Zero, SndMemory | SndAsync | SndNoDefault); }
            catch
            {
            }
        }

        private static void Tone(List<short> buf, int rate, double freq, double seconds, double volume, double pause)
        {
            int n = (int)(rate * seconds);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / n;
                double env = Math.Min(1.0, t * 8.0) * Math.Min(1.0, (1.0 - t) * 4.0);
                buf.Add((short)(Math.Sin(2.0 * Math.PI * freq * i / rate) * 32767.0 * volume * env));
            }
            int p = (int)(rate * pause);
            for (int i = 0; i < p; i++) buf.Add(0);
        }

        private static byte[] Wav(List<short> samples, int rate)
        {
            int data = samples.Count * 2;
            byte[] b = new byte[44 + data];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0);
            BitConverter.GetBytes(36 + data).CopyTo(b, 4);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(b, 8);
            BitConverter.GetBytes(16).CopyTo(b, 16);
            BitConverter.GetBytes((short)1).CopyTo(b, 20);
            BitConverter.GetBytes((short)1).CopyTo(b, 22);
            BitConverter.GetBytes(rate).CopyTo(b, 24);
            BitConverter.GetBytes(rate * 2).CopyTo(b, 28);
            BitConverter.GetBytes((short)2).CopyTo(b, 32);
            BitConverter.GetBytes((short)16).CopyTo(b, 34);
            Encoding.ASCII.GetBytes("data").CopyTo(b, 36);
            BitConverter.GetBytes(data).CopyTo(b, 40);
            for (int i = 0; i < samples.Count; i++) BitConverter.GetBytes(samples[i]).CopyTo(b, 44 + i * 2);
            return b;
        }
    }

    // Помощник обновления. Запускается копией программы из временной папки,
    // когда основная программа уже закрывается: дожидается её выхода, заменяет
    // файлы и запускает новую версию. Настройки и журналы не трогает.
    internal static class Updater
    {
        private const string ApplySwitch = "--apply-update";

        private static string Folder
        {
            get { return Path.Combine(Path.GetTempPath(), "SlonobassAgent-update"); }
        }

        private static string LogFile
        {
            get { return Path.Combine(Folder, "update.log"); }
        }

        private static void Note(string text)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + "\r\n",
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        public static bool Handles(string[] args)
        {
            return args != null && args.Length >= 4
                && string.Equals(args[0], ApplySwitch, StringComparison.Ordinal);
        }

        public static void Apply(string sourceRoot, string installDir, string pidText)
        {
            Note("Начало обновления. Источник: " + sourceRoot + "; установка: " + installDir);
            WaitForParent(pidText);
            try
            {
                // Копия прошлой версии программы — на случай, если новая
                // не запустится: файл можно вернуть вручную.
                string current = Path.Combine(installDir, "Slonobass-Agent.exe");
                if (File.Exists(current)) File.Copy(current, current + ".old", true);
            }
            catch
            {
            }
            try
            {
                string source = sourceRoot;
                string nested = Path.Combine(sourceRoot, "Slonobass-Agent");
                if (Directory.Exists(nested)) source = nested;
                int copied = CopyTree(source, installDir);
                Note("Скопировано файлов: " + copied.ToString() + ".");
            }
            catch (Exception ex)
            {
                Note("ОШИБКА при замене файлов: " + ex.ToString());
            }
            StartApp(installDir);
            Cleanup(sourceRoot);
        }

        private static void WaitForParent(string pidText)
        {
            int pid;
            if (pidText == null || !int.TryParse(pidText, out pid) || pid <= 0) return;
            DateTime until = DateTime.Now.AddSeconds(90);
            while (DateTime.Now < until)
            {
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        if (p.HasExited) break;
                    }
                }
                catch
                {
                    break;
                }
                Thread.Sleep(250);
            }
            // Дать Windows отпустить файлы после закрытия программы.
            Thread.Sleep(1200);
        }

        private static int CopyTree(string source, string target)
        {
            int copied = 0;
            string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                string rel = files[i].Substring(source.Length).TrimStart('\\', '/');
                string low = rel.Replace('/', '\\').ToLowerInvariant();
                // Настройки движка и журналы пользователя не трогаем.
                if (low.StartsWith("projects\\", StringComparison.Ordinal)) continue;
                if (low.StartsWith("log\\", StringComparison.Ordinal)) continue;
                string dst = Path.Combine(target, rel);
                if (File.Exists(dst) && SameFile(files[i], dst)) continue;
                string dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                CopyWithRetry(files[i], dst);
                copied++;
            }
            return copied;
        }

        private static bool SameFile(string a, string b)
        {
            try
            {
                FileInfo fa = new FileInfo(a);
                FileInfo fb = new FileInfo(b);
                return fa.Length == fb.Length
                    && Math.Abs((fa.LastWriteTimeUtc - fb.LastWriteTimeUtc).TotalSeconds) < 2;
            }
            catch
            {
                return false;
            }
        }

        private static void CopyWithRetry(string source, string target)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    File.Copy(source, target, true);
                    return;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                Thread.Sleep(400);
            }
            File.Copy(source, target, true);
        }

        private static void StartApp(string installDir)
        {
            try
            {
                string exe = Path.Combine(installDir, "Slonobass-Agent.exe");
                if (!File.Exists(exe))
                {
                    Note("Не найден запускаемый файл: " + exe);
                    return;
                }
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                psi.WorkingDirectory = installDir;
                psi.UseShellExecute = true;
                Process.Start(psi);
                Note("Программа запущена заново.");
            }
            catch (Exception ex)
            {
                Note("ОШИБКА при запуске программы: " + ex.ToString());
            }
        }

        private static void Cleanup(string stagingRoot)
        {
            try
            {
                if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
            }
            catch
            {
            }
            try
            {
                string zip = Path.Combine(Folder, "update.zip");
                if (File.Exists(zip)) File.Delete(zip);
            }
            catch
            {
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (Updater.Handles(args))
            {
                Updater.Apply(args[1], args[2], args[3]);
                return;
            }

            try
            {
                ServicePointManager.SecurityProtocol =
                    ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;
            }
            catch
            {
            }

            Log.Init(AppDomain.CurrentDomain.BaseDirectory);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += new ThreadExceptionEventHandler(OnThreadException);
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(OnUnhandled);

            Application.Run(new ChatForm());
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            ReportFatal(e.Exception);
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            ReportFatal(e.ExceptionObject as Exception);
        }

        private static void ReportFatal(Exception ex)
        {
            Log.Write("CRASH", ex == null ? "Неизвестная ошибка" : ex.ToString());
            string where = Log.Folder == null ? "папку программы" : "папку " + Log.Folder;
            try
            {
                MessageBox.Show(
                    "Программа столкнулась с ошибкой."
                    + (ex == null ? "" : "\r\n\r\n" + ex.Message)
                    + "\r\n\r\nЗапись сохранена в " + where
                    + ". Отправьте файл журнала разработчику.",
                    "Slonobass agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
            }
        }
    }
}
