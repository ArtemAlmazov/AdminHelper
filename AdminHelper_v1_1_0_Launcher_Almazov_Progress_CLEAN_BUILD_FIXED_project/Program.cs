using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdminHelper;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static void Main()
    {
        try
        {
            _mutex = new Mutex(true, "AdminHelper_Launcher_Edition_AAlmazov", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "Admin Helper уже запущен.",
                    "Admin Helper",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (_, e) => CrashLogger.Show(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    CrashLogger.Show(ex);
            };

            // RemoteAccess: remote access / status check before launcher startup.
            AccessCheckResult startupAccess = AppStatusGate.CheckStartupAccessAsync().GetAwaiter().GetResult();

            if (!startupAccess.Allowed)
            {
                using var blocked = AccessStatusForm.Create(startupAccess.Title, startupAccess.Message, startupAccess.TelegramUrl);
                Application.Run(blocked);
                return;
            }

            Application.Run(new LauncherForm());
        }
        catch (Exception ex)
        {
            CrashLogger.Show(ex);
        }
        finally
        {
            try
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }
}

public sealed class LauncherForm : Form
{
    private const string AppVersion = "1.1.0";
    private const string StableVersionText = AppVersion;
    private const string GitHubOwner = "ArtemAlmazov";
    private const string GitHubRepo = "AdminHelper";
    private const string ReleaseAssetName = "AdminHelper.exe";
    private const string SiteUrl = "https://artemalmazov.github.io/AdminHelper/";

    private readonly Panel _content = new();
    private readonly List<NavButton> _navButtons = new();
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly System.Windows.Forms.Timer _accessCheckTimer = new();

    private IntPtr _keyboardHook = IntPtr.Zero;
    private OverlayForm? _overlay;
    private NotifyIcon? _tray;
    private Label? _bottomStatus;
    private bool _overlayVisible;
    private bool _f9Down;
    private bool _isClosing;
    private bool _isAccessCheckRunning;
    private bool _accessLockShown;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_F9 = 0x78;
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private static readonly IntPtr HTCAPTION = new(2);

    public LauncherForm()
    {
        Text = "Admin Helper";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 730);
        MinimumSize = new Size(1180, 730);
        MaximumSize = new Size(1180, 730);
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Ui.Bg;
        ForeColor = Color.White;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular);
        Icon = ResourceLoader.LoadIcon("app.ico");

        _keyboardProc = KeyboardHookCallback;
        MouseDown += DragWindow;
        _accessCheckTimer.Interval = (int)TimeSpan.FromMinutes(3).TotalMilliseconds;
        _accessCheckTimer.Tick += async (_, _) => await PeriodicAccessCheckAsync();

        BuildWindowChrome();
        BuildSidebar();

        _content.Location = new Point(278, 58);
        _content.Size = new Size(860, 632);
        _content.BackColor = Color.Transparent;
        Controls.Add(_content);

        _bottomStatus = new Label
        {
            Text = $"Установлена актуальная версия: {StableVersionText}",
            Location = new Point(42, 694),
            Size = new Size(720, 24),
            BackColor = Color.Transparent,
            ForeColor = Ui.Green,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        Controls.Add(_bottomStatus);

        CreateTrayIcon();
        ShowPage("Главная");
    }

    private void BuildWindowChrome()
    {
        var topDragArea = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(ClientSize.Width, 38),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Ui.Bg
        };
        topDragArea.MouseDown += DragWindow;
        Controls.Add(topDragArea);

        var topButtons = new RoundedPanel
        {
            Location = new Point(1076, 10),
            Size = new Size(72, 26),
            BackColor = Color.FromArgb(10, 16, 26),
            BorderColor = Color.FromArgb(64, 88, 120),
            BorderRadius = 12
        };

        var minimize = MakeWindowIconButton(CreateMinimizeIcon(), 6, 3, false);
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;

        var close = MakeWindowIconButton(CreateCloseIcon(), 38, 3, true);
        close.Click += (_, _) => SafeExit();

        topButtons.Controls.Add(minimize);
        topButtons.Controls.Add(close);
        Controls.Add(topButtons);
        topButtons.BringToFront();
    }

    private void BuildSidebar()
    {
        var sidebar = new RoundedPanel
        {
            Location = new Point(28, 58),
            Size = new Size(222, 632),
            BackColor = Color.FromArgb(8, 13, 22),
            BorderColor = Color.FromArgb(48, 70, 98),
            BorderRadius = 28
        };

        var logoFrame = new RoundedPanel
        {
            Location = new Point(22, 22),
            Size = new Size(58, 58),
            BackColor = Color.FromArgb(5, 10, 18),
            BorderColor = Ui.Gold,
            BorderRadius = 29
        };

        var logo = new PictureBox
        {
            Image = ResourceLoader.LoadImage("avatar.png"),
            Location = new Point(4, 4),
            Size = new Size(50, 50),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent
        };
        logoFrame.Controls.Add(logo);

        var title = MakeLabel("ADMIN HELPER", 92, 24, 116, 22, 10.2f, FontStyle.Bold, Color.White);
        var version = MakeLabel("Launcher Edition", 92, 47, 120, 20, 8.6f, FontStyle.Bold, Ui.Blue);

        sidebar.Controls.Add(logoFrame);
        sidebar.Controls.Add(title);
        sidebar.Controls.Add(version);

        string[] pages = { "Главная", "Правила", "Команды", "Подсказка", "Обновления", "Настройки", "О программе" };
        string[] icons = { "⌂", "▣", ">", "?", "↻", "⚙", "i" };

        int y = 112;
        for (int i = 0; i < pages.Length; i++)
        {
            var nav = new NavButton(pages[i], icons[i])
            {
                Location = new Point(18, y),
                Size = new Size(186, 44)
            };

            nav.Click += (_, _) => ShowPage(nav.PageName);
            sidebar.Controls.Add(nav);
            _navButtons.Add(nav);
            y += 52;
        }

        var line = new Panel
        {
            Location = new Point(28, 526),
            Size = new Size(166, 1),
            BackColor = Color.FromArgb(42, 58, 82)
        };

        var statusDot = new StatusDot
        {
            Location = new Point(30, 550),
            Size = new Size(10, 10)
        };

        var status = MakeLabel("Система активна", 50, 542, 150, 26, 9F, FontStyle.Bold, Ui.Muted);
        var author = MakeLabel("Product by A.Almazov", 28, 586, 170, 24, 8.5f, FontStyle.Regular, Color.FromArgb(120, 136, 158));

        sidebar.Controls.Add(line);
        sidebar.Controls.Add(statusDot);
        sidebar.Controls.Add(status);
        sidebar.Controls.Add(author);

        Controls.Add(sidebar);
    }

    private void ShowPage(string page)
    {
        foreach (var nav in _navButtons)
            nav.Active = nav.PageName == page;

        _content.Controls.Clear();

        switch (page)
        {
            case "Главная":
                BuildHomePage();
                break;
            case "Правила":
                BuildRulesPage();
                break;
            case "Команды":
                BuildCommandsPage();
                break;
            case "Подсказка":
                BuildHintPage();
                break;
            case "Обновления":
                BuildUpdatesPage();
                break;
            case "Настройки":
                BuildSettingsPage();
                break;
            case "О программе":
                BuildAboutPage();
                break;
        }
    }


    private void BuildHomePage()
    {
        AddPageHeader("Главная", "Премиальный лаунчер для Admin Helper: быстрый доступ к подсказке, правилам и базовым действиям администратора.");

        var hero = new RoundedPanel
        {
            Location = new Point(0, 82),
            Size = new Size(860, 210),
            BackColor = Color.FromArgb(11, 18, 30),
            BorderColor = Color.FromArgb(62, 92, 126),
            BorderRadius = 30
        };

        hero.Controls.Add(MakeLabel("Admin Helper", 28, 26, 420, 42, 26F, FontStyle.Bold, Ui.Gold));
        hero.Controls.Add(MakeLabel("Launcher Edition", 30, 68, 220, 24, 11F, FontStyle.Bold, Ui.Blue));
        hero.Controls.Add(MakeLabel("Полноценное стартовое окно для администрации CRASH CRMP. Открывай подсказку по F9, переходи к правилам, копируй базовые команды и проверяй обновления из одного меню.", 30, 102, 455, 60, 10.4f, FontStyle.Regular, Ui.Muted));
        hero.Controls.Add(MakeBadge("ОФИЦИАЛЬНАЯ ПОДГОТОВКА К РЕЛИЗУ", 30, 170, 260, 30, Ui.Green));

        var openHint = MakeActionButton("Открыть подсказку", 520, 30, 290, 46, true);
        openHint.Click += (_, _) => ShowOverlay();
        var updates = MakeActionButton("Проверить обновления", 520, 86, 290, 42, false);
        updates.Click += async (_, _) => await CheckUpdatesAsync(manual: true);
        var site = MakeActionButton("Сайт программы", 520, 136, 138, 40, false);
        site.Click += (_, _) => OpenUrl(SiteUrl);
        var tg = MakeActionButton("Telegram-канал", 672, 136, 138, 40, false);
        tg.Click += (_, _) => OpenUrl(AppLinks.TelegramUrl);

        hero.Controls.Add(openHint);
        hero.Controls.Add(updates);
        hero.Controls.Add(site);
        hero.Controls.Add(tg);
        _content.Controls.Add(hero);

        _content.Controls.Add(FeatureCard("Быстрый старт", "F9", "Моментальное открытие подсказки администратора прямо во время игры.", 0, 316, Ui.Gold));
        _content.Controls.Add(FeatureCard("Статус системы", "ONLINE", "Лаунчер, сайт и основные разделы готовы к работе и тестированию.", 290, 316, Ui.Green));
        _content.Controls.Add(FeatureCard("Доступ к базе", "RULES", "Команды, регламент и полезные разделы собраны в одном удобном окне.", 580, 316, Ui.Blue));

        var lower = new RoundedPanel
        {
            Location = new Point(0, 482),
            Size = new Size(860, 110),
            BackColor = Color.FromArgb(10, 16, 28),
            BorderColor = Color.FromArgb(48, 70, 98),
            BorderRadius = 24
        };

        lower.Controls.Add(MakeLabel("Стартовые сценарии", 24, 16, 240, 28, 15F, FontStyle.Bold, Color.White));
        lower.Controls.Add(MakeLabel("Запусти подсказку, скопируй нужную команду, затем быстро проверь обновления или перейди на сайт программы.", 24, 46, 540, 42, 9.7f, FontStyle.Regular, Ui.Muted));
        lower.Controls.Add(MakeBadge("ПОДСКАЗКА", 590, 20, 110, 28, Ui.Gold));
        lower.Controls.Add(MakeBadge("КОМАНДЫ", 712, 20, 120, 28, Ui.Blue));
        lower.Controls.Add(MakeBadge("UPDATES", 650, 58, 110, 28, Ui.Green));
        _content.Controls.Add(lower);
    }

    private void BuildRulesPage()
    {
        AddPageHeader("Правила", "Быстрый макет справочника правил. В дальнейшем сюда можно добавить полноценный поиск.");

        var search = new TextBox
        {
            Location = new Point(0, 82),
            Size = new Size(860, 34),
            BackColor = Color.FromArgb(8, 13, 22),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10.5F, FontStyle.Regular),
            Text = "Поиск по правилам: DM, DB, MG, PG..."
        };
        _content.Controls.Add(search);

        string[] categories =
        {
            "Общие правила",
            "Игровой процесс",
            "Общение",
            "Администрация",
            "Наказания",
            "Жалобы"
        };

        int x = 0;
        int y = 140;
        foreach (string category in categories)
        {
            _content.Controls.Add(SmallInfoCard(category, "Категория регламента", x, y));
            x += 288;
            if (x > 580)
            {
                x = 0;
                y += 112;
            }
        }

        var panel = new RoundedPanel
        {
            Location = new Point(0, 390),
            Size = new Size(860, 190),
            BackColor = Color.FromArgb(10, 16, 28),
            BorderColor = Color.FromArgb(48, 70, 98),
            BorderRadius = 24
        };

        panel.Controls.Add(MakeLabel("Пример пункта регламента", 24, 20, 520, 28, 15F, FontStyle.Bold, Ui.Gold));
        panel.Controls.Add(MakeLabel("DM — нанесение урона или убийство игрока без достаточной IC-причины.\nНаказание зависит от ситуации, количества игроков и тяжести нарушения.\n\nРаздел можно расширить: поиск, категории, избранные пункты и быстрые шаблоны ответов.", 24, 58, 780, 110, 10F, FontStyle.Regular, Ui.Muted));
        _content.Controls.Add(panel);
    }


    private void BuildCommandsPage()
    {
        AddPageHeader("Команды", "Базовый набор команд администратора для быстрого копирования и ориентира во время работы.");

        string[,] commands =
        {
            { "/pm [id] [текст]", "Ответ игроку в личные сообщения" },
            { "/mute [id] [мин] [причина]", "Выдать текстовый мут" },
            { "/vipmute [id] [мин] [причина]", "Выдать VIP-мут" },
            { "/rmute [id] [мин] [причина]", "Заблокировать игроку /report" },
            { "/vmute [id] [мин] [причина]", "Выдать voice mute" },
            { "/jail [id] [мин] [причина]", "Посадить игрока в деморган" },
            { "/warn [id] [причина]", "Выдать предупреждение" },
            { "/kick [id] [причина]", "Кикнуть игрока с сервера" },
            { "/ban [id] [дни] [причина]", "Заблокировать аккаунт" },
            { "/sp [id]", "Начать слежку за игроком" },
            { "/goto [id]", "Телепортироваться к игроку" },
            { "/gethere [id]", "Телепортировать игрока к себе" }
        };

        int x = 0;
        int y = 84;
        for (int i = 0; i < commands.GetLength(0); i++)
        {
            _content.Controls.Add(CommandRow(commands[i, 0], commands[i, 1], x, y));
            x += 436;
            if (x > 436)
            {
                x = 0;
                y += 76;
            }
        }

        var note = new RoundedPanel
        {
            Location = new Point(0, 546),
            Size = new Size(860, 64),
            BackColor = Color.FromArgb(10, 16, 28),
            BorderColor = Color.FromArgb(48, 70, 98),
            BorderRadius = 20
        };
        note.Controls.Add(MakeLabel("Важно", 22, 10, 100, 20, 11F, FontStyle.Bold, Ui.Gold));
        note.Controls.Add(MakeLabel("Набор команд можно потом расширить под конкретный сервер. Сейчас сюда вынесены базовые и самые нужные команды для старта.", 22, 30, 810, 22, 9.3f, FontStyle.Regular, Ui.Muted));
        _content.Controls.Add(note);
    }

    private void BuildHintPage()
    {
        AddPageHeader("Подсказка", "Управление подсказкой администратора и статусом горячей клавиши F9.");

        var panel = new RoundedPanel
        {
            Location = new Point(0, 90),
            Size = new Size(860, 260),
            BackColor = Color.FromArgb(12, 19, 31),
            BorderColor = Color.FromArgb(54, 82, 112),
            BorderRadius = 28
        };

        var key = new RoundedPanel
        {
            Location = new Point(32, 40),
            Size = new Size(150, 150),
            BackColor = Color.FromArgb(8, 12, 20),
            BorderColor = Ui.Gold,
            BorderRadius = 24
        };

        key.Controls.Add(MakeLabel("КЛАВИША", 0, 24, 150, 22, 8.6f, FontStyle.Bold, Ui.Gold, ContentAlignment.MiddleCenter));
        key.Controls.Add(MakeLabel("F9", 0, 54, 150, 72, 48F, FontStyle.Bold, Color.White, ContentAlignment.MiddleCenter));

        panel.Controls.Add(key);
        panel.Controls.Add(MakeLabel("Подсказка администратора", 214, 48, 520, 34, 20F, FontStyle.Bold, Color.White));
        panel.Controls.Add(MakeLabel("Нажми F9 или кнопку ниже, чтобы открыть/закрыть подсказку поверх экрана. Если в игре не открывается — запусти программу от имени администратора.", 216, 90, 560, 54, 10.3f, FontStyle.Regular, Ui.Muted));

        var open = MakeActionButton("Показать / скрыть", 216, 166, 210, 44, true);
        open.Click += (_, _) => ToggleOverlay();

        var close = MakeActionButton("Закрыть подсказку", 442, 166, 190, 44, false);
        close.Click += (_, _) => HideOverlay();

        var test = MakeActionButton("Статус", 648, 166, 130, 44, false);
        test.Click += (_, _) =>
        {
            using var form = SimpleStatusForm.Info(
                "Статус подсказки",
                "F9 активен\nКартинка подсказки загружена\nВерсия программы: " + AppVersion);
            form.ShowDialog(this);
        };

        panel.Controls.Add(open);
        panel.Controls.Add(close);
        panel.Controls.Add(test);
        _content.Controls.Add(panel);

        _content.Controls.Add(SmallInfoCard("F9 активен", "Глобальная горячая клавиша", 0, 382));
        _content.Controls.Add(SmallInfoCard("Поверх окон", "Подсказка открывается TopMost", 288, 382));
        _content.Controls.Add(SmallInfoCard("Курсор", "Click-through режим сохранён", 576, 382));
    }

    private void BuildUpdatesPage()
    {
        AddPageHeader("Обновления", "Проверка актуальной версии и быстрый переход к релизу.");

        var box = new RoundedPanel
        {
            Location = new Point(0, 92),
            Size = new Size(860, 250),
            BackColor = Color.FromArgb(12, 19, 31),
            BorderColor = Ui.Green,
            BorderRadius = 28
        };

        box.Controls.Add(MakeLabel("Обновления проверены", 44, 42, 560, 38, 24F, FontStyle.Bold, Color.White));
        box.Controls.Add(MakeLabel($"Установлена актуальная версия: {StableVersionText}", 46, 92, 560, 28, 12F, FontStyle.Bold, Ui.Green));
        box.Controls.Add(MakeLabel("Admin Helper готов к работе. Проверку можно запустить вручную через кнопку ниже.", 46, 128, 640, 46, 10F, FontStyle.Regular, Ui.Muted));

        var check = MakeActionButton("Проверить обновления", 46, 184, 230, 46, true);
        check.Click += async (_, _) => await CheckUpdatesAsync(manual: true);

        var release = MakeActionButton("Страница версии", 292, 184, 170, 46, false);
        release.Click += (_, _) => OpenUrl($"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest");

        box.Controls.Add(check);
        box.Controls.Add(release);
        _content.Controls.Add(box);
    }


    private void BuildSettingsPage()
    {
        AddPageHeader("Настройки", "Безопасные параметры интерфейса и основные режимы работы лаунчера без перегруза лишними элементами.");

        var top = new RoundedPanel
        {
            Location = new Point(0, 86),
            Size = new Size(860, 110),
            BackColor = Color.FromArgb(11, 18, 30),
            BorderColor = Color.FromArgb(62, 92, 126),
            BorderRadius = 26
        };
        top.Controls.Add(MakeLabel("Профиль конфигурации", 24, 18, 260, 28, 15F, FontStyle.Bold, Color.White));
        top.Controls.Add(MakeLabel("Все параметры ниже отображаются в виде готового стабильного профиля для основной версии программы.", 24, 48, 520, 24, 9.6f, FontStyle.Regular, Ui.Muted));
        top.Controls.Add(MakeBadge("СТАБИЛЬНЫЙ ПРОФИЛЬ", 632, 20, 180, 32, Ui.Green));
        top.Controls.Add(MakeBadge("UI READY", 690, 60, 122, 28, Ui.Blue));
        _content.Controls.Add(top);

        _content.Controls.Add(SettingRow("Подсказка по F9", "Глобальная горячая клавиша для открытия и скрытия подсказки.", 0, 220, Ui.Gold));
        _content.Controls.Add(SettingRow("Поверх всех окон", "Подсказка запускается в overlay-режиме и не мешает мышке.", 0, 298, Ui.Blue));
        _content.Controls.Add(SettingRow("Красивое окно обновлений", "Проверка версий открывается в фирменном стиле программы.", 0, 376, Ui.Green));
        _content.Controls.Add(SettingRow("Системный трей", "Программа работает с иконкой в трее и быстрыми действиями.", 0, 454, Ui.Gold));
        _content.Controls.Add(SettingRow("Сайт и Telegram", "Переход к ресурсу программы доступен прямо из лаунчера.", 0, 532, Ui.Blue));
    }

    private void BuildAboutPage()
    {
        AddPageHeader("О программе", "Информация о продукте, авторе и назначении Admin Helper.");

        var box = new RoundedPanel
        {
            Location = new Point(0, 92),
            Size = new Size(860, 350),
            BackColor = Color.FromArgb(12, 19, 31),
            BorderColor = Color.FromArgb(54, 82, 112),
            BorderRadius = 28
        };

        box.Controls.Add(MakeLabel("Admin Helper Launcher Edition", 34, 34, 690, 36, 23F, FontStyle.Bold, Ui.Gold));
        box.Controls.Add(MakeLabel("Инструмент для администрации CRMP/RP-проектов.\n\nПервая версия подготовлена для CRASH CRMP. Лаунчер объединяет подсказку по F9, справочник правил, команды, обновления, сайт и Telegram-канал.\n\nАвтор: Artem_Almazov\nTelegram: @ahelperAlmazov\nDiscord: artemiiooo", 36, 88, 730, 178, 10.5f, FontStyle.Regular, Ui.Muted));

        var site = MakeActionButton("Открыть сайт", 36, 282, 180, 44, true);
        site.Click += (_, _) => OpenUrl(SiteUrl);

        var tg = MakeActionButton("Telegram", 232, 282, 160, 44, false);
        tg.Click += (_, _) => OpenUrl(AppLinks.TelegramUrl);

        box.Controls.Add(site);
        box.Controls.Add(tg);
        _content.Controls.Add(box);
    }

    private void AddPageHeader(string title, string subtitle)
    {
        _content.Controls.Add(MakeLabel(title, 0, 0, 520, 42, 28F, FontStyle.Bold, Color.White));
        _content.Controls.Add(MakeLabel(subtitle, 2, 46, 720, 24, 10F, FontStyle.Regular, Ui.Muted));
        _content.Controls.Add(MakeBadge("LAUNCHER EDITION", 684, 10, 160, 32, Ui.Blue));
    }

    private RoundedPanel MetricCard(string title, string value, string text, int x, int y, Color accent)
    {
        var card = new RoundedPanel
        {
            Location = new Point(x, y),
            Size = new Size(202, 132),
            BackColor = Color.FromArgb(12, 19, 31),
            BorderColor = Color.FromArgb(48, 70, 98),
            BorderRadius = 22
        };

        card.Controls.Add(MakeLabel(title, 18, 18, 160, 20, 9.5f, FontStyle.Bold, Ui.Muted));
        card.Controls.Add(MakeLabel(value, 18, 42, 160, 42, 24F, FontStyle.Bold, accent));
        card.Controls.Add(MakeLabel(text, 18, 88, 164, 24, 9F, FontStyle.Regular, Ui.Muted));
        return card;
    }


    private RoundedPanel SmallInfoCard(string title, string text, int x, int y)
    {
        var card = new RoundedPanel
        {
            Location = new Point(x, y),
            Size = new Size(270, 88),
            BackColor = Color.FromArgb(12, 19, 31),
            BorderColor = Color.FromArgb(56, 80, 112),
            BorderRadius = 20
        };

        var accent = new Panel
        {
            Location = new Point(18, 18),
            Size = new Size(4, 48),
            BackColor = Ui.Blue
        };

        card.Controls.Add(accent);
        card.Controls.Add(MakeLabel(title, 34, 16, 210, 24, 11F, FontStyle.Bold, Color.White));
        card.Controls.Add(MakeLabel(text, 34, 44, 220, 24, 9F, FontStyle.Regular, Ui.Muted));
        return card;
    }

    private RoundedPanel FeatureCard(string title, string value, string text, int x, int y, Color accent)
    {
        var card = new RoundedPanel
        {
            Location = new Point(x, y),
            Size = new Size(280, 144),
            BackColor = Color.FromArgb(12, 19, 31),
            BorderColor = Color.FromArgb(56, 80, 112),
            BorderRadius = 24
        };

        var chip = new RoundedPanel
        {
            Location = new Point(18, 18),
            Size = new Size(62, 30),
            BackColor = Color.FromArgb(22, 28, 42),
            BorderColor = Color.FromArgb(90, accent),
            BorderRadius = 15
        };
        chip.Controls.Add(MakeLabel("LIVE", 0, 0, 62, 30, 8.6f, FontStyle.Bold, accent, ContentAlignment.MiddleCenter));

        card.Controls.Add(chip);
        card.Controls.Add(MakeLabel(title, 18, 58, 190, 22, 11F, FontStyle.Bold, Color.White));
        card.Controls.Add(MakeLabel(value, 18, 82, 220, 28, 18F, FontStyle.Bold, accent));
        card.Controls.Add(MakeLabel(text, 18, 110, 244, 24, 9F, FontStyle.Regular, Ui.Muted));
        return card;
    }

    private RoundedPanel CommandRow(string command, string description, int x, int y)
    {
        var row = new RoundedPanel
        {
            Location = new Point(x, y),
            Size = new Size(424, 64),
            BackColor = Color.FromArgb(12, 19, 31),
            BorderColor = Color.FromArgb(48, 70, 98),
            BorderRadius = 18
        };

        var copy = MakeActionButton("Копировать", 306, 14, 104, 34, false);
        copy.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(command);
            SetStatus("Команда скопирована: " + command, Ui.Green);
        };

        row.Controls.Add(MakeLabel(command, 18, 10, 270, 22, 10F, FontStyle.Bold, Ui.Blue));
        row.Controls.Add(MakeLabel(description, 18, 34, 280, 18, 8.8f, FontStyle.Regular, Ui.Muted));
        row.Controls.Add(copy);
        return row;
    }

    private RoundedPanel SettingRow(string title, string description, int x, int y, Color accent)
    {
        var row = new RoundedPanel
        {
            Location = new Point(x, y),
            Size = new Size(860, 64),
            BackColor = Color.FromArgb(12, 19, 31),
            BorderColor = Color.FromArgb(48, 70, 98),
            BorderRadius = 18
        };

        var dot = new Panel
        {
            Location = new Point(18, 18),
            Size = new Size(8, 28),
            BackColor = accent
        };

        var badge = new RoundedPanel
        {
            Location = new Point(718, 16),
            Size = new Size(120, 32),
            BackColor = Color.FromArgb(18, 24, 38),
            BorderColor = Color.FromArgb(95, accent),
            BorderRadius = 16
        };
        badge.Controls.Add(MakeLabel("АКТИВНО", 0, 0, 120, 32, 9F, FontStyle.Bold, accent, ContentAlignment.MiddleCenter));

        row.Controls.Add(dot);
        row.Controls.Add(MakeLabel(title, 38, 12, 360, 22, 11F, FontStyle.Bold, Color.White));
        row.Controls.Add(MakeLabel(description, 38, 34, 560, 18, 9F, FontStyle.Regular, Ui.Muted));
        row.Controls.Add(badge);
        return row;
    }

    private Label MakeLabel(string text, int x, int y, int w, int h, float size, FontStyle style, Color color, ContentAlignment align = ContentAlignment.MiddleLeft)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            ForeColor = color,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", size, style),
            TextAlign = align
        };
    }

    private Label MakeBadge(string text, int x, int y, int w, int h, Color accent)
    {
        var badge = new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            ForeColor = accent,
            BackColor = Color.FromArgb(12, 19, 31),
            Font = new Font("Segoe UI", 8.8F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter
        };

        badge.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(90, accent), 1);
            e.Graphics.DrawRoundedRectangle(pen, new Rectangle(0, 0, badge.Width - 1, badge.Height - 1), h / 2);
        };

        return badge;
    }

    private Button MakeActionButton(string text, int x, int y, int w, int h, bool primary)
    {
        Color back = primary ? Ui.Gold : Color.FromArgb(31, 39, 54);
        Color fore = primary ? Color.FromArgb(5, 7, 10) : Color.White;
        Color hover = primary ? Color.FromArgb(255, 201, 82) : Color.FromArgb(43, 53, 72);

        var button = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            BackColor = back,
            ForeColor = fore,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            TabStop = false
        };

        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(90, 112, 145);
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(230, 156, 22) : Color.FromArgb(24, 30, 42);
        return button;
    }

    private Button MakeWindowIconButton(Image icon, int x, int y, bool danger)
    {
        Color back = danger ? Color.FromArgb(24, 17, 22) : Color.FromArgb(16, 23, 34);
        Color hover = danger ? Color.FromArgb(72, 27, 37) : Color.FromArgb(31, 43, 61);

        var button = new Button
        {
            Text = "",
            Image = icon,
            ImageAlign = ContentAlignment.MiddleCenter,
            Location = new Point(x, y),
            Size = new Size(28, 20),
            BackColor = back,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            TabStop = false
        };

        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = danger ? Color.FromArgb(125, 78, 92) : Color.FromArgb(63, 87, 116);
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = danger ? Color.FromArgb(96, 36, 48) : Color.FromArgb(22, 31, 45);
        return button;
    }

    private static Bitmap CreateMinimizeIcon()
    {
        Bitmap bmp = new(18, 18);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var glow = new SolidBrush(Color.FromArgb(26, 92, 142, 225));
        g.FillEllipse(glow, 3, 3, 12, 12);

        using var pen = new Pen(Color.FromArgb(220, 232, 245), 2.0f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        g.DrawLine(pen, 5, 9, 13, 9);
        return bmp;
    }

    private static Bitmap CreateCloseIcon()
    {
        Bitmap bmp = new(18, 18);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var glow = new SolidBrush(Color.FromArgb(24, 255, 114, 114));
        g.FillEllipse(glow, 3, 3, 12, 12);

        using var pen = new Pen(Color.FromArgb(255, 210, 218), 2.0f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        g.DrawLine(pen, 6, 6, 12, 12);
        g.DrawLine(pen, 12, 6, 6, 12);
        return bmp;
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        PrepareOverlay();
        InstallKeyboardHook();
        SetStatus($"Установлена актуальная версия: {StableVersionText}", Ui.Green);
        _accessCheckTimer.Start();

        // Тихая проверка обновлений после запуска.
        // Если на GitHub есть версия выше текущей — появится окно установки.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1600);
            if (!IsDisposed && IsHandleCreated)
            {
                try
                {
                    BeginInvoke(new Action(async () => await CheckUpdatesAsync(manual: false)));
                }
                catch
                {
                    // ignore
                }
            }
        });
    }

    private void PrepareOverlay()
    {
        try
        {
            if (_overlay == null || _overlay.IsDisposed)
                _overlay = new OverlayForm();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка подготовки подсказки: " + ex.Message, Color.OrangeRed);
            CrashLogger.Write(ex);
        }
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
            return;

        using Process currentProcess = Process.GetCurrentProcess();
        using ProcessModule? module = currentProcess.MainModule;

        IntPtr moduleHandle = GetModuleHandle(module?.ModuleName);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);

            if (vkCode == VK_F9)
            {
                int message = wParam.ToInt32();

                if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
                {
                    if (!_f9Down)
                    {
                        _f9Down = true;
                        BeginInvoke(new Action(ToggleOverlay));
                    }
                }
                else if (message == WM_KEYUP || message == WM_SYSKEYUP)
                {
                    _f9Down = false;
                }
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void ToggleOverlay()
    {
        if (_overlayVisible)
            HideOverlay();
        else
            ShowOverlay();
    }

    private void ShowOverlay()
    {
        try
        {
            if (_overlay == null || _overlay.IsDisposed)
                _overlay = new OverlayForm();

            _overlay.ShowOverlayNoActivate();
            _overlayVisible = true;
            SetStatus("Подсказка открыта. Нажми F9 ещё раз, чтобы закрыть.", Ui.Green);
        }
        catch (Exception ex)
        {
            _overlayVisible = false;
            SetStatus("Не удалось открыть подсказку.", Color.OrangeRed);
            CrashLogger.Write(ex);

            using var form = SimpleStatusForm.Error(
                "Не удалось открыть подсказку",
                "Ошибка: " + ex.Message);
            form.ShowDialog(this);
        }
    }

    private void HideOverlay()
    {
        try
        {
            _overlay?.HideOverlayNoActivate();
            _overlayVisible = false;
            SetStatus("Подсказка закрыта. Нажми F9, чтобы открыть её снова.", Color.White);
        }
        catch (Exception ex)
        {
            CrashLogger.Write(ex);
            SetStatus("Ошибка закрытия подсказки: " + ex.Message, Color.OrangeRed);
        }
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        SetStatus("Проверяю обновления...", Ui.Gold);

        try
        {
            UpdateReleaseInfo? update = await GitHubUpdateChecker.GetLatestReleaseAsync(GitHubOwner, GitHubRepo, ReleaseAssetName);

            if (update == null)
            {
                SetStatus($"Установлена актуальная версия: {StableVersionText}", Ui.Green);

                if (manual)
                {
                    using var form = SimpleStatusForm.Info(
                        "Обновления проверены",
                        $"Установлена актуальная версия: {StableVersionText}");
                    form.ShowDialog(this);
                }

                return;
            }

            Version current = Version.Parse(StableVersionText);
            Version latest = update.Version;

            if (latest > current)
            {
                SetStatus("Найдена новая версия: " + latest, Ui.Gold);

                using var form = UpdateAvailableForm.Create(latest.ToString(), update.ReleaseUrl, update.DownloadUrl);
                form.ShowDialog(this);
            }
            else
            {
                SetStatus($"Установлена актуальная версия: {StableVersionText}", Ui.Green);

                if (manual)
                {
                    using var form = SimpleStatusForm.Info(
                        "Обновления проверены",
                        $"Установлена актуальная версия: {StableVersionText}");
                    form.ShowDialog(this);
                }
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Write(ex);
            SetStatus("Не удалось проверить обновления.", Color.OrangeRed);

            if (manual)
            {
                using var form = SimpleStatusForm.Error(
                    "Проверка не выполнена",
                    "Не удалось проверить обновления.\n\n" + ex.Message);
                form.ShowDialog(this);
            }
        }
    }

    private async Task PeriodicAccessCheckAsync()
    {
        if (_isClosing || _accessLockShown || _isAccessCheckRunning)
            return;

        _isAccessCheckRunning = true;

        try
        {
            AccessRuntimeCheckResult result = await AppStatusGate.CheckRuntimeAccessAsync();

            if (result.IsDisabled && !_accessLockShown && !_isClosing)
            {
                _accessLockShown = true;
                HideOverlay();

                using var form = AccessStatusForm.Create(
                    result.Title,
                    result.Message,
                    result.TelegramUrl);
                form.ShowDialog(this);

                SafeExit();
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Write(ex);
        }
        finally
        {
            _isAccessCheckRunning = false;
        }
    }

    private void CreateTrayIcon()
    {
        _tray = new NotifyIcon
        {
            Icon = ResourceLoader.LoadIcon("app.ico"),
            Text = "Admin Helper",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть меню", null, (_, _) =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        });

        menu.Items.Add("Показать / скрыть подсказку", null, (_, _) => ToggleOverlay());
        menu.Items.Add("Сайт", null, (_, _) => OpenUrl(SiteUrl));
        menu.Items.Add("Telegram", null, (_, _) => OpenUrl(AppLinks.TelegramUrl));
        menu.Items.Add("Выход", null, (_, _) => SafeExit());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        };
    }

    private void SafeExit()
    {
        if (_isClosing)
            return;

        _isClosing = true;

        try
        {
            _accessCheckTimer.Stop();

            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            if (_overlay is { IsDisposed: false })
            {
                try
                {
                    _overlay.HideOverlayNoActivate();
                    _overlay.Dispose();
                }
                catch
                {
                    // ignore
                }
            }

            if (_tray != null)
                _tray.Visible = false;
        }
        catch
        {
            // ignore
        }

        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_isClosing)
        {
            _isClosing = true;
            SafeExit();
            return;
        }

        try
        {
            _accessCheckTimer.Stop();
            _accessCheckTimer.Dispose();

            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            if (_tray != null)
                _tray.Visible = false;
        }
        catch
        {
            // ignore
        }

        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        Rectangle paintRect = ClientRectangle;
        if (paintRect.Width <= 0 || paintRect.Height <= 0)
            return;

        using var bg = new LinearGradientBrush(paintRect, Color.FromArgb(4, 7, 13), Color.FromArgb(9, 18, 31), 105f);
        e.Graphics.FillRectangle(bg, ClientRectangle);

        using var gold = new SolidBrush(Ui.Gold);
        e.Graphics.FillRectangle(gold, 0, 0, Width, 3);

        using var border = new Pen(Color.FromArgb(90, Ui.Gold), 1);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        using var blueGlow = new SolidBrush(Color.FromArgb(30, Ui.Blue));
        e.Graphics.FillEllipse(blueGlow, Width - 430, -210, 700, 700);

        using var goldGlow = new SolidBrush(Color.FromArgb(20, Ui.Gold));
        e.Graphics.FillEllipse(goldGlow, -220, 420, 520, 520);

        using var gridPen = new Pen(Color.FromArgb(9, 255, 255, 255), 1);
        for (int x = 0; x < Width; x += 48)
            e.Graphics.DrawLine(gridPen, x, 0, x, Height);
        for (int y = 0; y < Height; y += 48)
            e.Graphics.DrawLine(gridPen, 0, y, Width, y);
    }

    private void SetStatus(string text, Color color)
    {
        if (_bottomStatus == null)
            return;

        _bottomStatus.Text = text;
        _bottomStatus.ForeColor = color;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            CrashLogger.Write(ex);
        }
    }
}

public static class AppLinks
{
    public const string TelegramUrl = "https://t.me/ahelperAlmazov";
    public const string AppStatusUrl = "https://artemalmazov.github.io/AdminHelper/app-status.json";
}

public sealed record AccessCheckResult(bool Allowed, string Title, string Message, string TelegramUrl);

public sealed record AccessRuntimeCheckResult(bool IsDisabled, string Title, string Message, string TelegramUrl);

public static class AppStatusGate
{
    private const string RemoteAccessStatusTag = "adminhelper_status";
    private const string DefaultBlockedTitle = "Доступ временно закрыт автором";
    private const string DefaultBlockedText = "Сервер проверки Admin Helper сообщает, что доступ временно закрыт автором. Следите за новостями в Telegram-канале.";
    private const string HardModeTitle = "Не удалось проверить доступ";
    private const string HardModeText = "Admin Helper не смог подключиться к серверу проверки. Проверьте интернет-соединение или следите за новостями в Telegram-канале.";
    private const string DefaultTelegram = AppLinks.TelegramUrl;

    public static async Task<AccessCheckResult> CheckStartupAccessAsync()
    {
        try
        {
            AppStatusDto status = await LoadStatusAsync();
            if (status.Enabled)
                return new AccessCheckResult(true, "", "", DefaultTelegram);

            string title = string.IsNullOrWhiteSpace(status.Title) ? DefaultBlockedTitle : status.Title.Trim();
            string message = string.IsNullOrWhiteSpace(status.Text) ? DefaultBlockedText : status.Text.Trim();
            string telegram = string.IsNullOrWhiteSpace(status.Telegram) ? DefaultTelegram : status.Telegram.Trim();

            return new AccessCheckResult(false, title, message, telegram);
        }
        catch (Exception ex)
        {
            CrashLogger.Write(ex);
            return new AccessCheckResult(false, HardModeTitle, HardModeText, DefaultTelegram);
        }
    }

    public static async Task<AccessRuntimeCheckResult> CheckRuntimeAccessAsync()
    {
        try
        {
            AppStatusDto status = await LoadStatusAsync();
            if (status.Enabled)
                return new AccessRuntimeCheckResult(false, "", "", DefaultTelegram);

            string title = string.IsNullOrWhiteSpace(status.Title) ? DefaultBlockedTitle : status.Title.Trim();
            string message = string.IsNullOrWhiteSpace(status.Text) ? DefaultBlockedText : status.Text.Trim();
            string telegram = string.IsNullOrWhiteSpace(status.Telegram) ? DefaultTelegram : status.Telegram.Trim();
            return new AccessRuntimeCheckResult(true, title, message, telegram);
        }
        catch (Exception ex)
        {
            CrashLogger.Write(ex);
            return new AccessRuntimeCheckResult(false, "", "", DefaultTelegram);
        }
    }

    private static async Task<AppStatusDto> LoadStatusAsync()
    {
        long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string url = $"{AppLinks.AppStatusUrl}?t={stamp}";

        using HttpClient client = new();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AdminHelperStatusGate/1.1.0 ({RemoteAccessStatusTag})");

        string json = await client.GetStringAsync(url);
        AppStatusPayload? payload = JsonSerializer.Deserialize<AppStatusPayload>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (payload == null)
            throw new InvalidOperationException("Сервер проверки Admin Helper вернул пустой ответ.");

        if (payload.Enabled == null)
            throw new InvalidOperationException("app-status.json не содержит обязательное поле enabled.");

        return new AppStatusDto(
            payload.Enabled.Value,
            payload.Title ?? payload.MessageTitle,
            payload.Text ?? payload.Message,
            payload.Telegram);
    }

    private sealed record AppStatusDto(bool Enabled, string? Title, string? Text, string? Telegram);

    private sealed class AppStatusPayload
    {
        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("messageTitle")]
        public string? MessageTitle { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("telegram")]
        public string? Telegram { get; set; }
    }
}

public sealed class AccessStatusForm : Form

{
    private readonly string _titleText;
    private readonly string _bodyText;
    private readonly string _telegramUrl;

    private Button? _closeButton;
    private Button? _telegramButton;
    private Button? _topCloseButton;

    private AccessStatusForm(string titleText, string bodyText, string telegramUrl)
    {
        _titleText = string.IsNullOrWhiteSpace(titleText)
            ? "Доступ временно закрыт"
            : titleText;

        _bodyText = string.IsNullOrWhiteSpace(bodyText)
            ? "Сервер проверки Admin Helper сообщает, что доступ временно закрыт."
            : bodyText;

        _telegramUrl = string.IsNullOrWhiteSpace(telegramUrl)
            ? AppLinks.TelegramUrl
            : telegramUrl;

        Text = "Admin Helper";
        Size = new Size(760, 470);
        MinimumSize = new Size(760, 470);
        MaximumSize = new Size(760, 470);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Ui.Bg;
        ForeColor = Color.White;
        DoubleBuffered = true;
        Icon = ResourceLoader.LoadIcon("app.ico");
        KeyPreview = true;

        BuildUi();

        KeyDown += AccessStatusForm_KeyDown;
    }

    public static AccessStatusForm Create(string title, string body, string telegramUrl)
        => new(title, body, telegramUrl);

    private void BuildUi()
    {
        var topDrag = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(Width, 42),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.Transparent
        };
        topDrag.MouseDown += DragWindow;
        Controls.Add(topDrag);

        _topCloseButton = new Button
        {
            Text = "✕",
            Location = new Point(706, 10),
            Size = new Size(34, 26),
            BackColor = Color.FromArgb(20, 26, 38),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            TabStop = false
        };
        _topCloseButton.FlatAppearance.BorderSize = 1;
        _topCloseButton.FlatAppearance.BorderColor = Color.FromArgb(95, 120, 150);
        _topCloseButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(90, 35, 45);
        _topCloseButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(120, 40, 50);
        _topCloseButton.Click += (_, _) => ForceClose();
        Controls.Add(_topCloseButton);

        var mainCard = new RoundedPanel
        {
            Location = new Point(28, 52),
            Size = new Size(704, 332),
            BackColor = Color.FromArgb(12, 18, 30),
            BorderColor = Color.FromArgb(72, 98, 132),
            BorderRadius = 28
        };

        var iconWrap = new RoundedPanel
        {
            Location = new Point(28, 26),
            Size = new Size(74, 74),
            BackColor = Color.FromArgb(18, 24, 38),
            BorderColor = Ui.Gold,
            BorderRadius = 37
        };

        var iconLabel = new Label
        {
            Text = "!",
            Location = new Point(0, 0),
            Size = new Size(74, 74),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 28F, FontStyle.Bold),
            ForeColor = Ui.Gold,
            BackColor = Color.Transparent
        };
        iconWrap.Controls.Add(iconLabel);

        var badge = new Label
        {
            Text = "ACCESS CONTROL",
            Location = new Point(120, 28),
            Size = new Size(180, 28),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 8.8F, FontStyle.Bold),
            ForeColor = Ui.Blue,
            BackColor = Color.FromArgb(18, 24, 38)
        };
        badge.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(90, Ui.Blue), 1);
            e.Graphics.DrawRoundedRectangle(pen, new Rectangle(0, 0, badge.Width - 1, badge.Height - 1), 14);
        };

        var title = new Label
        {
            Text = _titleText,
            Location = new Point(120, 68),
            Size = new Size(540, 42),
            Font = new Font("Segoe UI", 20F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.Transparent
        };

        var subtitle = new Label
        {
            Text = "Проверка удалённого доступа к Admin Helper",
            Location = new Point(122, 112),
            Size = new Size(420, 24),
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Ui.Muted,
            BackColor = Color.Transparent
        };

        var infoBox = new RoundedPanel
        {
            Location = new Point(28, 152),
            Size = new Size(648, 136),
            BackColor = Color.FromArgb(8, 13, 22),
            BorderColor = Color.FromArgb(58, 80, 112),
            BorderRadius = 22
        };

        var infoTitle = new Label
        {
            Text = "Сообщение системы",
            Location = new Point(22, 16),
            Size = new Size(220, 24),
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Ui.Gold,
            BackColor = Color.Transparent
        };

        var body = new Label
        {
            Text = _bodyText,
            Location = new Point(22, 46),
            Size = new Size(600, 72),
            Font = new Font("Segoe UI", 10.4F, FontStyle.Regular),
            ForeColor = Ui.Muted,
            BackColor = Color.Transparent
        };

        infoBox.Controls.Add(infoTitle);
        infoBox.Controls.Add(body);

        mainCard.Controls.Add(iconWrap);
        mainCard.Controls.Add(badge);
        mainCard.Controls.Add(title);
        mainCard.Controls.Add(subtitle);
        mainCard.Controls.Add(infoBox);

        Controls.Add(mainCard);

        _telegramButton = new Button
        {
            Text = "Открыть Telegram",
            Location = new Point(28, 402),
            Size = new Size(220, 44),
            BackColor = Ui.Gold,
            ForeColor = Color.FromArgb(5, 7, 10),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold)
        };
        _telegramButton.FlatAppearance.BorderSize = 0;
        _telegramButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 202, 84);
        _telegramButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(232, 160, 22);
        _telegramButton.Click += (_, _) => OpenUrl(_telegramUrl);

        _closeButton = new Button
        {
            Text = "Закрыть",
            Location = new Point(574, 402),
            Size = new Size(158, 44),
            BackColor = Color.FromArgb(31, 39, 54),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold)
        };
        _closeButton.FlatAppearance.BorderSize = 1;
        _closeButton.FlatAppearance.BorderColor = Color.FromArgb(95, 120, 150);
        _closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(43, 53, 72);
        _closeButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(24, 30, 42);
        _closeButton.Click += (_, _) => ForceClose();

        Controls.Add(_telegramButton);
        Controls.Add(_closeButton);
    }

    private void AccessStatusForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            ForceClose();
        }
    }

    private void ForceClose()
    {
        try
        {
            DialogResult = DialogResult.OK;
        }
        catch
        {
            // ignore
        }

        Close();
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private static readonly IntPtr HTCAPTION = new(2);

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        Rectangle paintRect = ClientRectangle;
        if (paintRect.Width <= 0 || paintRect.Height <= 0)
            return;

        using var bg = new LinearGradientBrush(
            paintRect,
            Color.FromArgb(4, 7, 13),
            Color.FromArgb(11, 20, 34),
            110f);
        e.Graphics.FillRectangle(bg, ClientRectangle);

        using var topLine = new SolidBrush(Ui.Gold);
        e.Graphics.FillRectangle(topLine, 0, 0, Width, 3);

        using var border = new Pen(Color.FromArgb(90, Ui.Gold), 1);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        using var blueGlow = new SolidBrush(Color.FromArgb(24, Ui.Blue));
        e.Graphics.FillEllipse(blueGlow, Width - 290, -120, 320, 320);

        using var goldGlow = new SolidBrush(Color.FromArgb(18, Ui.Gold));
        e.Graphics.FillEllipse(goldGlow, -100, 280, 300, 300);

        using var gridPen = new Pen(Color.FromArgb(8, 255, 255, 255), 1);
        for (int x = 0; x < Width; x += 42)
            e.Graphics.DrawLine(gridPen, x, 0, x, Height);

        for (int y = 0; y < Height; y += 42)
            e.Graphics.DrawLine(gridPen, 0, y, Width, y);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            CrashLogger.Write(ex);
        }
    }
}
public sealed class OverlayForm : Form
{
    private readonly Image _image;

    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int HWND_TOPMOST = -1;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_SHOWNA = 8;
    private const int SW_HIDE = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public OverlayForm()
    {
        _image = ResourceLoader.LoadImage("reglament.png");

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Fuchsia;
        TransparencyKey = Color.Fuchsia;
        TopMost = true;
        Cursor = Cursors.Default;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void ShowOverlayNoActivate()
    {
        Rectangle screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

        StartPosition = FormStartPosition.Manual;
        Bounds = screen;
        TopMost = true;
        WindowState = FormWindowState.Normal;

        if (!IsHandleCreated)
            _ = Handle;

        if (!Visible)
            Show();

        SetWindowPos(Handle, HWND_TOPMOST, screen.X, screen.Y, screen.Width, screen.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        ShowWindow(Handle, SW_SHOWNA);
        Invalidate();
        Update();
    }

    public void HideOverlayNoActivate()
    {
        if (IsHandleCreated)
        {
            ShowWindow(Handle, SW_HIDE);
            Hide();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Color.Fuchsia);
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(_image, ClientRectangle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _image.Dispose();

        base.Dispose(disposing);
    }
}

public static class GitHubUpdateChecker
{
    public static async Task<UpdateReleaseInfo?> GetLatestReleaseAsync(string owner, string repo, string assetName)
    {
        using HttpClient http = new();
        http.Timeout = TimeSpan.FromSeconds(12);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AdminHelperLauncher/1.1.0");

        // Берём именно список выпусков, а не /latest.
        // Так программа видит v1.1.1 даже если GitHub не считает его latest по своим правилам.
        string url = $"https://api.github.com/repos/{owner}/{repo}/releases";
        string json = await http.GetStringAsync(url);

        List<GitHubRelease>? releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (releases == null || releases.Count == 0)
            return null;

        UpdateReleaseInfo? best = null;

        foreach (GitHubRelease release in releases)
        {
            if (release.Draft)
                continue;

            if (string.IsNullOrWhiteSpace(release.TagName))
                continue;

            string versionText = release.TagName.Trim().TrimStart('v', 'V');

            if (!Version.TryParse(versionText, out Version? version))
                continue;

            string? download = release.Assets?
                .FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))
                ?.BrowserDownloadUrl;

            // Если AdminHelper.exe не прикреплён, этот выпуск не подходит для автоустановки.
            if (string.IsNullOrWhiteSpace(download))
                continue;

            UpdateReleaseInfo candidate = new(version, release.HtmlUrl ?? "", download);

            if (best == null || candidate.Version > best.Version)
                best = candidate;
        }

        return best;
    }
}


public sealed record UpdateReleaseInfo(Version Version, string ReleaseUrl, string DownloadUrl);

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}


public sealed class SimpleStatusForm : Form
{
    private readonly bool _success;
    private readonly string _titleText;
    private readonly string _bodyText;

    private SimpleStatusForm(bool success, string title, string body)
    {
        _success = success;
        _titleText = title;
        _bodyText = body;

        Text = "Admin Helper";
        Size = new Size(590, 330);
        MinimumSize = new Size(590, 330);
        MaximumSize = new Size(590, 330);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Ui.Bg;
        ForeColor = Color.White;
        DoubleBuffered = true;
        Icon = ResourceLoader.LoadIcon("app.ico");

        var card = new RoundedPanel
        {
            Location = new Point(28, 30),
            Size = new Size(534, 220),
            BackColor = Color.FromArgb(14, 20, 31),
            BorderColor = success ? Ui.Green : Ui.Gold,
            BorderRadius = 26
        };

        card.Controls.Add(MakeStaticLabel(title, 34, 34, 440, 34, 20F, FontStyle.Bold, Color.White));
        card.Controls.Add(MakeStaticLabel(body, 36, 82, 455, 86, 10.4F, FontStyle.Regular, success ? Ui.Green : Ui.Gold));

        var ok = new Button
        {
            Text = "Понятно",
            Location = new Point(410, 268),
            Size = new Size(176, 42),
            BackColor = success ? Ui.Green : Ui.Gold,
            ForeColor = Color.FromArgb(5, 7, 10),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            DialogResult = DialogResult.OK,
            Cursor = Cursors.Hand
        };
        ok.FlatAppearance.BorderSize = 0;

        Controls.Add(card);
        Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = ok;
    }

    public static SimpleStatusForm Info(string title, string body) => new(true, title, body);
    public static SimpleStatusForm Error(string title, string body) => new(false, title, body);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        Rectangle paintRect = ClientRectangle;
        if (paintRect.Width <= 0 || paintRect.Height <= 0)
            return;

        using var bg = new LinearGradientBrush(paintRect, Color.FromArgb(5, 8, 14), Color.FromArgb(14, 22, 36), 110f);
        e.Graphics.FillRectangle(bg, ClientRectangle);

        using var top = new SolidBrush(_success ? Ui.Green : Ui.Gold);
        e.Graphics.FillRectangle(top, 0, 0, Width, 3);

        using var border = new Pen(Color.FromArgb(90, _success ? Ui.Green : Ui.Gold), 1);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    private static Label MakeStaticLabel(string text, int x, int y, int w, int h, float size, FontStyle style, Color color, ContentAlignment align = ContentAlignment.MiddleLeft)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            ForeColor = color,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", size, style),
            TextAlign = align
        };
    }
}

public sealed class UpdateAvailableForm : Form
{
    private readonly string _version;
    private readonly string _releaseUrl;
    private readonly string _downloadUrl;
    private readonly Label _statusLabel;
    private readonly Label _percentLabel;
    private readonly RoundedPanel _progressTrack;
    private readonly Panel _progressFill;
    private readonly Button _installButton;
    private readonly Button _releaseButton;
    private readonly Button _closeButton;

    private UpdateAvailableForm(string version, string releaseUrl, string downloadUrl)
    {
        _version = version;
        _releaseUrl = releaseUrl;
        _downloadUrl = downloadUrl;

        Text = "Admin Helper — обновление";
        Size = new Size(690, 420);
        MinimumSize = new Size(690, 420);
        MaximumSize = new Size(690, 420);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Ui.Bg;
        ForeColor = Color.White;
        DoubleBuffered = true;
        Icon = ResourceLoader.LoadIcon("app.ico");

        var card = new RoundedPanel
        {
            Location = new Point(28, 30),
            Size = new Size(634, 284),
            BackColor = Color.FromArgb(14, 20, 31),
            BorderColor = Ui.Gold,
            BorderRadius = 26
        };

        card.Controls.Add(MakeStaticLabel("Доступна новая версия", 34, 30, 480, 34, 20F, FontStyle.Bold, Color.White));
        card.Controls.Add(MakeStaticLabel("Новая версия: " + version, 36, 76, 455, 28, 11F, FontStyle.Bold, Ui.Gold));
        card.Controls.Add(MakeStaticLabel("Лаунчер получит новую сборку от Алмазова, заменит текущий AdminHelper.exe и запустит обновлённую программу.", 36, 112, 505, 58, 10F, FontStyle.Regular, Ui.Muted));

        _statusLabel = MakeStaticLabel(
            string.IsNullOrWhiteSpace(downloadUrl)
                ? "Файл обновления не найден. Проверь выпуск и файл AdminHelper.exe."
                : "Готово к установке обновления.",
            36, 178, 505, 34, 9.6F, FontStyle.Bold,
            string.IsNullOrWhiteSpace(downloadUrl) ? Color.OrangeRed : Ui.Green);

        card.Controls.Add(_statusLabel);

        _percentLabel = MakeStaticLabel("0%", 546, 178, 58, 26, 11F, FontStyle.Bold, Ui.Gold, ContentAlignment.MiddleRight);

        _progressTrack = new RoundedPanel
        {
            Location = new Point(36, 216),
            Size = new Size(560, 26),
            BackColor = Color.FromArgb(8, 13, 22),
            BorderColor = Color.FromArgb(64, 92, 126),
            BorderRadius = 13
        };

        _progressFill = new Panel
        {
            Location = new Point(3, 3),
            Size = new Size(0, 20),
            BackColor = Ui.Gold
        };

        _progressTrack.Controls.Add(_progressFill);
        card.Controls.Add(_percentLabel);
        card.Controls.Add(_progressTrack);
        card.Controls.Add(MakeStaticLabel("Во время установки не закрывай программу вручную.", 36, 252, 520, 22, 8.8F, FontStyle.Regular, Color.FromArgb(135, 150, 172)));

        _installButton = new Button
        {
            Text = "Установить обновление",
            Location = new Point(30, 346),
            Size = new Size(230, 42),
            BackColor = Ui.Gold,
            ForeColor = Color.FromArgb(5, 7, 10),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Enabled = !string.IsNullOrWhiteSpace(downloadUrl)
        };
        _installButton.FlatAppearance.BorderSize = 0;
        _installButton.Click += async (_, _) => await InstallUpdateAsync();

        _releaseButton = new Button
        {
            Text = "Страница версии",
            Location = new Point(278, 346),
            Size = new Size(150, 42),
            BackColor = Color.FromArgb(31, 39, 54),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _releaseButton.FlatAppearance.BorderSize = 1;
        _releaseButton.FlatAppearance.BorderColor = Color.FromArgb(90, 112, 145);
        _releaseButton.Click += (_, _) => OpenUrl(_releaseUrl);

        _closeButton = new Button
        {
            Text = "Позже",
            Location = new Point(510, 346),
            Size = new Size(150, 42),
            BackColor = Color.FromArgb(31, 39, 54),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.OK
        };
        _closeButton.FlatAppearance.BorderSize = 1;
        _closeButton.FlatAppearance.BorderColor = Color.FromArgb(90, 112, 145);

        Controls.Add(card);
        Controls.Add(_installButton);
        Controls.Add(_releaseButton);
        Controls.Add(_closeButton);
        CancelButton = _closeButton;
    }

    public static UpdateAvailableForm Create(string version, string releaseUrl, string downloadUrl) => new(version, releaseUrl, downloadUrl);

    private async Task InstallUpdateAsync()
    {
        _installButton.Enabled = false;
        _releaseButton.Enabled = false;
        _closeButton.Enabled = false;
        SetProgress("Подготавливаем обновление от Алмазова...", 5, Ui.Gold);

        try
        {
            await AutoUpdater.DownloadAndInstallAsync(_downloadUrl, (text, percent) =>
            {
                if (!IsDisposed)
                    SetProgress(text, percent, Ui.Gold);
            });

            SetProgress("Готово. Admin Helper будет перезапущен...", 100, Ui.Green);
            await Task.Delay(700);

            Application.Exit();
        }
        catch (Exception ex)
        {
            CrashLogger.Write(ex);

            SetProgress("Не удалось установить обновление.", 0, Color.OrangeRed);

            _installButton.Enabled = true;
            _releaseButton.Enabled = true;
            _closeButton.Enabled = true;

            using var form = SimpleStatusForm.Error(
                "Ошибка автообновления",
                ex.Message);
            form.ShowDialog(this);
        }
    }

    private void SetProgress(string text, int percent, Color color)
    {
        percent = Math.Max(0, Math.Min(100, percent));

        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
        _percentLabel.Text = percent + "%";

        int maxWidth = Math.Max(0, _progressTrack.Width - 6);
        int fillWidth = (int)Math.Round(maxWidth * (percent / 100.0));

        _progressFill.Width = fillWidth;
        _progressFill.Height = Math.Max(1, _progressTrack.Height - 6);
        _progressFill.BackColor = percent >= 100 ? Ui.Green : Ui.Gold;

        _progressFill.Refresh();
        _progressTrack.Refresh();
        _statusLabel.Refresh();
        _percentLabel.Refresh();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        Rectangle paintRect = ClientRectangle;
        if (paintRect.Width <= 0 || paintRect.Height <= 0)
            return;

        using var bg = new LinearGradientBrush(paintRect, Color.FromArgb(5, 8, 14), Color.FromArgb(14, 22, 36), 110f);
        e.Graphics.FillRectangle(bg, ClientRectangle);

        using var top = new SolidBrush(Ui.Gold);
        e.Graphics.FillRectangle(top, 0, 0, Width, 3);

        using var border = new Pen(Color.FromArgb(90, Ui.Gold), 1);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    private static Label MakeStaticLabel(string text, int x, int y, int w, int h, float size, FontStyle style, Color color, ContentAlignment align = ContentAlignment.MiddleLeft)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            ForeColor = color,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", size, style),
            TextAlign = align
        };
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            CrashLogger.Write(ex);
        }
    }
}

public static class AutoUpdater
{
    public static async Task DownloadAndInstallAsync(string downloadUrl, Action<string, int>? status = null)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("В выпуске не найден файл AdminHelper.exe.");

        if (!downloadUrl.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ссылка обновления повреждена или ведёт не на файл программы.");

        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            currentExe = Application.ExecutablePath;

        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            throw new FileNotFoundException("Не удалось определить путь текущего AdminHelper.exe.");

        string updateDir = Path.Combine(Path.GetTempPath(), "AdminHelper_Update");
        Directory.CreateDirectory(updateDir);

        string newExe = Path.Combine(updateDir, "AdminHelper_new.exe");
        string updaterBat = Path.Combine(updateDir, "install_update.bat");

        status?.Invoke("Подключаемся к серверу обновлений...", 12);

        using HttpClient http = new();
        http.Timeout = TimeSpan.FromMinutes(4);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AdminHelperAutoUpdater/1.1");

        status?.Invoke("Скачиваем новую сборку Admin Helper...", 28);
        byte[] data = await http.GetByteArrayAsync(downloadUrl);
        status?.Invoke("Проверяем скачанный файл...", 72);

        if (data.Length < 1024 * 100)
            throw new InvalidOperationException("Скачанный файл слишком маленький. Возможно, в релизе прикреплён не AdminHelper.exe.");

        await File.WriteAllBytesAsync(newExe, data);

        status?.Invoke("Файл получен. Готовим установку от Алмазова...", 82);

        string currentDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;
        string logPath = Path.Combine(updateDir, "update_log.txt");
        int pid = Environment.ProcessId;

        string bat = $"""
@echo off
chcp 65001 >nul
echo Admin Helper update started > "{logPath}"
echo Waiting for process {pid} >> "{logPath}"
timeout /t 2 /nobreak >nul

:wait_process
tasklist /FI "PID eq {pid}" | find "{pid}" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait_process
)

echo Replacing file >> "{logPath}"
copy /Y "{newExe}" "{currentExe}" >> "{logPath}" 2>&1
if errorlevel 1 (
    echo Failed to replace file >> "{logPath}"
    pause
    exit /b 1
)

echo Starting new version >> "{logPath}"
cd /d "{currentDir}"
start "" "{currentExe}"

del "{newExe}" >nul 2>nul
del "%~f0" >nul 2>nul
""";

        await File.WriteAllTextAsync(updaterBat, bat);

        status?.Invoke("Запускаем установку и перезапуск программы...", 96);

        Process.Start(new ProcessStartInfo
        {
            FileName = updaterBat,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
}

public sealed class NavButton : Control
{
    private bool _hover;
    private bool _active;

    public string PageName { get; }
    private string IconText { get; }

    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            Invalidate();
        }
    }

    public NavButton(string pageName, string iconText)
    {
        PageName = pageName;
        IconText = iconText;
        BackColor = Color.FromArgb(8, 13, 22);
        ForeColor = Color.White;
        Cursor = Cursors.Hand;

        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        Color back = _active
            ? Color.FromArgb(40, 83, 135)
            : _hover
                ? Color.FromArgb(18, 28, 44)
                : Color.FromArgb(8, 13, 22);

        using var brush = new SolidBrush(back);
        e.Graphics.FillRoundedRectangle(brush, new Rectangle(0, 0, Width - 1, Height - 1), 14);

        if (_active || _hover)
        {
            using var pen = new Pen(_active ? Ui.Blue : Color.FromArgb(86, 108, 138), 1.2f);
            e.Graphics.DrawRoundedRectangle(pen, new Rectangle(0, 0, Width - 1, Height - 1), 14);
        }

        if (_active)
        {
            using var accent = new SolidBrush(Ui.Blue);
            e.Graphics.FillRoundedRectangle(accent, new Rectangle(4, 8, 4, Height - 17), 2);
        }

        Rectangle iconRect = new Rectangle(18, 11, 18, 18);
        Color iconColor = _active ? Color.White : (_hover ? Ui.Blue : Color.FromArgb(220, 230, 245));
        DrawIcon(e.Graphics, iconRect, iconColor);

        using var textFont = new Font("Segoe UI", 9.7F, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, PageName, textFont, new Rectangle(54, 10, 118, 24), _active ? Color.White : Ui.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private void DrawIcon(Graphics g, Rectangle r, Color color)
    {
        using var pen = new Pen(color, 1.9f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        switch (PageName)
        {
            case "Главная":
                g.DrawLine(pen, r.Left + 2, r.Top + 8, r.Left + 9, r.Top + 2);
                g.DrawLine(pen, r.Left + 9, r.Top + 2, r.Right - 2, r.Top + 8);
                g.DrawRectangle(pen, r.Left + 4, r.Top + 8, 10, 7);
                break;

            case "Правила":
                g.DrawRectangle(pen, r.Left + 3, r.Top + 2, 11, 14);
                g.DrawLine(pen, r.Left + 6, r.Top + 6, r.Right - 4, r.Top + 6);
                g.DrawLine(pen, r.Left + 6, r.Top + 10, r.Right - 4, r.Top + 10);
                break;

            case "Команды":
                g.DrawLine(pen, r.Left + 3, r.Top + 5, r.Left + 8, r.Top + 9);
                g.DrawLine(pen, r.Left + 3, r.Top + 13, r.Left + 8, r.Top + 9);
                g.DrawLine(pen, r.Left + 11, r.Top + 13, r.Right - 3, r.Top + 13);
                break;

            case "Подсказка":
                g.DrawEllipse(pen, r.Left + 2, r.Top + 1, 14, 14);
                using (var font = new Font("Segoe UI", 10F, FontStyle.Bold))
                    TextRenderer.DrawText(g, "?", font, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                break;

            case "Обновления":
                g.DrawArc(pen, r.Left + 2, r.Top + 2, 13, 13, 40, 260);
                g.DrawLine(pen, r.Left + 11, r.Top + 2, r.Left + 15, r.Top + 3);
                g.DrawLine(pen, r.Left + 15, r.Top + 3, r.Left + 13, r.Top + 7);
                break;

            case "Настройки":
                g.DrawEllipse(pen, r.Left + 5, r.Top + 5, 8, 8);
                g.DrawLine(pen, r.Left + 9, r.Top + 1, r.Left + 9, r.Top + 4);
                g.DrawLine(pen, r.Left + 9, r.Bottom - 4, r.Left + 9, r.Bottom - 1);
                g.DrawLine(pen, r.Left + 1, r.Top + 9, r.Left + 4, r.Top + 9);
                g.DrawLine(pen, r.Right - 4, r.Top + 9, r.Right - 1, r.Top + 9);
                g.DrawLine(pen, r.Left + 3, r.Top + 3, r.Left + 5, r.Top + 5);
                g.DrawLine(pen, r.Right - 3, r.Top + 3, r.Right - 5, r.Top + 5);
                g.DrawLine(pen, r.Left + 3, r.Bottom - 3, r.Left + 5, r.Bottom - 5);
                g.DrawLine(pen, r.Right - 3, r.Bottom - 3, r.Right - 5, r.Bottom - 5);
                break;

            case "О программе":
                g.DrawEllipse(pen, r.Left + 2, r.Top + 2, 14, 14);
                g.DrawLine(pen, r.Left + 9, r.Top + 7, r.Left + 9, r.Top + 12);
                g.DrawLine(pen, r.Left + 9, r.Top + 4, r.Left + 9, r.Top + 4);
                break;

            default:
                using (var font = new Font("Segoe UI", 10F, FontStyle.Bold))
                    TextRenderer.DrawText(g, IconText, font, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                break;
        }
    }
}

public sealed class RoundedPanel : Panel
{
    public int BorderRadius { get; set; } = 20;
    public Color BorderColor { get; set; } = Color.FromArgb(60, 80, 110);

    public RoundedPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(12, 18, 30);

        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (Width <= 1 || Height <= 1)
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        Rectangle rect = new(0, 0, Width - 1, Height - 1);
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        using GraphicsPath path = GraphicsExtensions.CreateRoundedRectangle(rect, BorderRadius);

        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);

        using var pen = new Pen(BorderColor, 1);
        e.Graphics.DrawPath(pen, path);
    }
}

public sealed class StatusDot : Control
{
    public StatusDot()
    {
        BackColor = Color.FromArgb(8, 13, 22);
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Ui.Green);
        e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
    }
}

public static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using GraphicsPath path = CreateRoundedRectangle(bounds, radius);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using GraphicsPath path = CreateRoundedRectangle(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    public static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();

        if (bounds.Width <= 0 || bounds.Height <= 0)
            return path;

        radius = Math.Max(1, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        int diameter = radius * 2;

        Rectangle arc = new(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);

        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);

        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);

        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }
}

public static class ResourceLoader
{
    public static Image LoadImage(string logicalName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using Stream? stream = assembly.GetManifestResourceStream(logicalName)
            ?? assembly.GetManifestResourceNames()
                .Where(name => name.EndsWith(logicalName, StringComparison.OrdinalIgnoreCase))
                .Select(name => assembly.GetManifestResourceStream(name))
                .FirstOrDefault(s => s != null);

        if (stream != null)
        {
            using var temp = Image.FromStream(stream);
            return new Bitmap(temp);
        }

        string local1 = Path.Combine(AppContext.BaseDirectory, "images", logicalName);
        if (File.Exists(local1))
            return new Bitmap(local1);

        string local2 = Path.Combine(AppContext.BaseDirectory, logicalName);
        if (File.Exists(local2))
            return new Bitmap(local2);

        throw new FileNotFoundException("Ресурс не найден: " + logicalName);
    }

    public static Icon LoadIcon(string logicalName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using Stream? stream = assembly.GetManifestResourceStream(logicalName)
            ?? assembly.GetManifestResourceNames()
                .Where(name => name.EndsWith(logicalName, StringComparison.OrdinalIgnoreCase))
                .Select(name => assembly.GetManifestResourceStream(name))
                .FirstOrDefault(s => s != null);

        if (stream == null)
            return SystemIcons.Application;

        using MemoryStream ms = new();
        stream.CopyTo(ms);
        ms.Position = 0;
        return new Icon(ms);
    }
}

public static class CrashLogger
{
    public static void Write(Exception ex)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "adminhelper_error.log");
            File.AppendAllText(path, DateTime.Now + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
            // ignore
        }
    }

    public static void Show(Exception ex)
    {
        Write(ex);

        try
        {
            MessageBox.Show(
                "Admin Helper столкнулся с ошибкой.\n\n" +
                ex.Message +
                "\n\nФайл adminhelper_error.log создан рядом с программой.",
                "Admin Helper",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // ignore
        }
    }
}

public static class Ui
{
    public static readonly Color Bg = Color.FromArgb(5, 8, 14);
    public static readonly Color Gold = Color.FromArgb(255, 184, 43);
    public static readonly Color Blue = Color.FromArgb(42, 168, 255);
    public static readonly Color Green = Color.FromArgb(102, 255, 154);
    public static readonly Color Muted = Color.FromArgb(172, 186, 207);
}
