using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using Container_V3;
using Container_V3.Modification;

namespace ContainerCC.Mobile;

/// <summary>
/// Две области в одном списке: файлы телефона и содержимое открытого контейнера.
/// .cc-файл открывается как папка — так же, как его показывает WCX-плагин в Total Commander.
///
/// Одиночное нажатие: папка — войти, .cc в списке телефона — открыть контейнер, файл — отметить.
/// Долгое нажатие: файл — открыть тем приложением, которое на телефоне назначено для такого типа
/// (внутри контейнера — сначала временное извлечение, затем открытие); папка и .cc в списке
/// телефона — отметить или снять отметку (отмеченный .cc нужен, чтобы удалить контейнер).
/// Аппаратная «Назад» — тот же путь вверх, что и строка «.. (выше)»; на корне общей памяти
/// приложение уходит на рабочий стол.
/// </summary>
[Activity(Label = "@string/app_name", MainLauncher = true, EnableOnBackInvokedCallback = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    private enum Mode { Device, Container }

    private const string PrefsName = "containercc";
    private const string PrefsPathKey = "last_path";
    private const int LegacyPermissionRequest = 101;

    private readonly ContainerService _service = new();
    private readonly List<ContainerService.Row> _rows = [];
    private AppSettings _settings = null!;
    private Android.Window.IOnBackInvokedCallback? _backCallback;

    private ListView _list = null!;
    private TextView _pathLabel = null!;
    private TextView _status = null!;
    private ProgressBar _progress = null!;
    private Button _stopButton = null!;

    private Mode _mode = Mode.Device;
    private string _devicePath = "";
    private string _containerPath = "";
    private string _containerDir = "";
    private HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private string _viewKey = "";   // чтобы отличать «изменились строки» от «перешли в другую папку»

    private CancellationTokenSource? _cts;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        _settings = new AppSettings(this);

        base.OnCreate(savedInstanceState);

        // Тема применяется до разметки: иначе экран на мгновение показал бы прежнюю.
        SetTheme(_settings.ThemeResource);
        SetContentView(Resource.Layout.activity_main);

        _list = FindViewById<ListView>(Resource.Id.fileList)!;
        _pathLabel = FindViewById<TextView>(Resource.Id.pathLabel)!;
        _status = FindViewById<TextView>(Resource.Id.statusText)!;
        _progress = FindViewById<ProgressBar>(Resource.Id.progressBar)!;
        _stopButton = FindViewById<Button>(Resource.Id.stopButton)!;

        FindViewById<Button>(Resource.Id.settingsButton)!.Click += (_, _) => ShowSettings();
        FindViewById<Button>(Resource.Id.refreshButton)!.Click += (_, _) => Refresh();
        FindViewById<Button>(Resource.Id.selectAllButton)!.Click += (_, _) => SelectAll();
        FindViewById<Button>(Resource.Id.actionsButton)!.Click += (_, _) => ShowActions();
        _stopButton.Click += (_, _) => _cts?.Cancel();

        _list.ItemClick += OnItemClick;
        _list.ItemLongClick += OnItemLongClick;

        RegisterBackHandling();

        _devicePath = RestorePath();
        if (string.IsNullOrWhiteSpace(_devicePath) || !Directory.Exists(_devicePath) || !InsideStorage(_devicePath))
            _devicePath = StorageAccess.StartDirectory();

        EnsureStorageAccess();
        Refresh();

        // Проверка ядра на самом Android: --es selftest 1 (результат в adb logcat -s ContainerCC).
        AndroidSelfTest.RunIfRequested(Intent);
    }

    // ---------- разрешение на общую память ----------

    private void EnsureStorageAccess()
    {
        if (StorageAccess.HasAllFilesAccess())
            return;

        if (!StorageAccess.NeedsAllFilesPermission())
        {
            RequestPermissions(
                ["android.permission.READ_EXTERNAL_STORAGE", "android.permission.WRITE_EXTERNAL_STORAGE"],
                LegacyPermissionRequest);
            return;
        }

        new AlertDialog.Builder(this)
            .SetTitle("Нужен доступ ко всем файлам")
            .SetMessage("Чтобы контейнеры лежали в общих папках телефона — там же, где их видит Total Commander, — включите приложению доступ ко всем файлам. Без него доступны только файлы внутри папки самого приложения.")
            .SetPositiveButton("Открыть настройки", (_, _) =>
            {
                try { StartActivity(StorageAccess.AllFilesAccessSettingsIntent()); }
                catch (Exception ex) { Say("Не удалось открыть настройки: " + ex.Message); }
            })
            .SetNegativeButton("Работать в папке приложения", (_, _) =>
            {
                _devicePath = StorageAccess.StartDirectory();
                Refresh();
            })
            .Show();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        Refresh();
    }

    // ---------- список ----------

    private void Refresh()
    {
        _rows.Clear();

        if (_mode == Mode.Device)
        {
            _pathLabel.Text = "Файлы телефона — " + _devicePath;

            if (Path.GetFullPath(_devicePath).TrimEnd('/') != "/")
                _rows.Add(new ContainerService.Row(".. (выше)", "", 0, true));

            try
            {
                foreach (string dir in Directory.GetDirectories(_devicePath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    _rows.Add(new ContainerService.Row(Path.GetFileName(dir) + "/", dir, 0, true));

                foreach (string file in Directory.GetFiles(_devicePath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    // Фильтр из настроек: «только контейнеры .cc» прячет прочие файлы (и резервные
                    // копии .cc.bak). Папки всегда остаются — без них некуда идти.
                    if (_settings.DeviceFilter == AppSettings.FilterContainers && !IsContainer(file))
                        continue;

                    long size = 0;
                    try { size = new FileInfo(file).Length; } catch (IOException) { }
                    _rows.Add(new ContainerService.Row(Path.GetFileName(file), file, size, false));
                }
            }
            catch (Exception ex)
            {
                Say("Не удалось прочитать папку: " + ex.Message);
            }
        }
        else
        {
            string where = _containerDir.Length == 0 ? "" : " — " + _containerDir;
            _pathLabel.Text = "Контейнер " + Path.GetFileName(_containerPath) + where;

            _rows.Add(new ContainerService.Row(".. (выше)", "", 0, true));

            try
            {
                _rows.AddRange(_service.Children(_containerPath, _containerDir));
            }
            catch (Exception ex)
            {
                Say("Контейнер не открылся: " + ex.Message);
                LeaveContainer();
                return;
            }
        }

        var display = new List<string>(_rows.Count);
        foreach (ContainerService.Row row in _rows)
        {
            if (row.Key.Length == 0)
            {
                display.Add(row.Name);
                continue;
            }

            string mark = _selected.Contains(row.Key) ? "☑ " : "☐ ";
            string kind = row.IsFolder ? "📁 " : (IsContainer(row.Key) ? "📦 " : "📄 ");
            string size = row.IsFolder ? "" : "  " + SizeText(row.Size);
            display.Add(mark + kind + row.Name + size);
        }

        // Список пересоздаётся целиком — так проще всего с отметками ☑/☐. Но ListView при смене
        // адаптера всегда показывает начало, и отметка файла в конце длинного списка перебрасывала
        // экран наверх: приходилось снова спускаться к тому же файлу. Поэтому положение прокрутки
        // возвращаем, пока не изменилась просматриваемая папка (отметка, удаление, добавление —
        // остаются на своём месте). При переходе в другую папку или в контейнер — начинаем с начала.
        int keepFirst = _list.FirstVisiblePosition;
        int keepOffset = 0;
        Android.Views.View? firstRow = keepFirst >= 0 ? _list.GetChildAt(0) : null;
        if (firstRow is not null)
            keepOffset = firstRow.Top;

        string viewKey = _mode + "|" + (_mode == Mode.Device
            ? _devicePath
            : _containerPath + "#" + _containerDir);
        bool sameView = viewKey == _viewKey;

        _list.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleListItem1, display);

        if (sameView && keepFirst > 0)
            _list.SetSelectionFromTop(Math.Min(keepFirst, Math.Max(0, _rows.Count - 1)), keepOffset);

        _viewKey = viewKey;
        _list.InvalidateViews();
    }

    private static bool IsContainer(string path) => path.EndsWith(".cc", StringComparison.OrdinalIgnoreCase);

    private void OnItemClick(object? sender, AdapterView.ItemClickEventArgs e)
    {
        ContainerService.Row row = _rows[e.Position];

        if (row.Key.Length == 0)
        {
            GoUp();
            return;
        }

        if (_mode == Mode.Device)
        {
            if (row.IsFolder)
            {
                EnterDeviceFolder(row.Key);
                return;
            }

            if (IsContainer(row.Key))
            {
                OpenContainer(row.Key);
                return;
            }
        }
        else if (row.IsFolder)
        {
            // Key папки внутри контейнера — уже полный путь от её корня, добавлять к нему текущую
            // папку нельзя: получится «docs\docs\sub».
            _containerDir = row.Key;
            _selected.Clear();
            Refresh();
            return;
        }

        Toggle(row.Key);
    }

    private void OnItemLongClick(object? sender, AdapterView.ItemLongClickEventArgs e)
    {
        ContainerService.Row row = _rows[e.Position];
        if (row.Key.Length == 0)
            return;

        // Долгое нажатие на файл — открыть тем приложением, которое на телефоне назначено для
        // такого типа: в списке телефона сразу, внутри контейнера — через временное извлечение.
        // Папки долгим нажатием отмечаются, как и .cc в списке телефона: .cc нужен отмеченным,
        // чтобы его удалить (внутри контейнера .cc отмечается коротким нажатием, как и прочие файлы).
        if (!row.IsFolder && !(_mode == Mode.Device && IsContainer(row.Key)))
        {
            if (_mode == Mode.Device)
                OpenWithOtherApp(row.Key);
            else
                OpenEntryFromContainer(row);

            e.Handled = true;
            return;
        }

        Toggle(row.Key);
        e.Handled = true;
    }

    /// <summary>
    /// Открыть файл телефона тем приложением, которое на телефоне считается «для такого типа».
    /// Файл передаём адресом content:// — так разрешает Android 7 и новее.
    /// </summary>
    private void OpenWithOtherApp(string path)
    {
        try
        {
            var intent = new Intent(Intent.ActionView);
            intent.SetData(FileOpenProvider.ContentUriFor(path));
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            Say("Нет приложения, которым можно открыть «" + Path.GetFileName(path) + "».");
        }
        catch (Exception error)
        {
            Say("Не удалось открыть «" + Path.GetFileName(path) + "»: " + error.Message);
        }
    }

    /// <summary>
    /// Открыть файл из контейнера той же чужой программой, что и файл на телефоне. Внутри контейнера
    /// файла на диске нет, поэтому он вытаскивается во временную папку приложения (там сохраняется
    /// и имя файла, и его папки — читалки ориентируются на расширение), а потом отдаётся адресу.
    /// </summary>
    private async void OpenEntryFromContainer(ContainerService.Row row)
    {
        string tempRoot = TempOpenRoot();
        string tempPath = TempPathFor(tempRoot, row.Key);

        long need = _service.EstimatedExtractBytes(_containerPath, new[] { row.Key });
        if (!await ConfirmSpaceAsync("временное извлечение для открытия", need, tempRoot))
            return;

        Run($"Извлечение «{row.Name}» для открытия…", true, token =>
        {
            ClearTempOpen(tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

            int done = _service.Extract(_containerPath, new[] { row.Key }, tempRoot, Report,
                () => token.IsCancellationRequested);

            if (done == 0 || !File.Exists(tempPath))
            {
                TryDelete(tempPath);
                return token.IsCancellationRequested
                    ? "Извлечение остановлено."
                    : "Не удалось извлечь «" + row.Name + "» из контейнера.";
            }

            RunOnUiThread(() => OpenWithOtherApp(tempPath));
            return "Извлечено для открытия: " + row.Name;
        });
    }

    /// <summary>Временная папка открытий: она в кэше приложения, и мы очищаем её перед каждым новым открытием.</summary>
    private static string TempOpenRoot()
    {
        string root = Path.Combine(Android.App.Application.Context.CacheDir!.Path!, "открытое");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string TempPathFor(string tempRoot, string entryKey)
    {
        string relative = entryKey.Replace('\\', '/').TrimStart('/');
        string root = Path.GetFullPath(tempRoot);
        string path = Path.GetFullPath(Path.Combine(root, relative));

        // Имя из повреждённого контейнера не должно выбраться за временную папку: тогда берём только имя файла.
        if (relative.Length == 0 || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            int cut = relative.LastIndexOf('/');
            path = Path.Combine(root, cut < 0 ? relative : relative[(cut + 1)..]);
        }

        return path;
    }

    private static void ClearTempOpen(string tempRoot)
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(tempRoot, "*", SearchOption.AllDirectories))
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            foreach (string file in Directory.GetFiles(tempRoot, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException)
        {
            // Старый мусор помешать открытию не должен.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Toggle(string key)
    {
        if (!_selected.Remove(key))
            _selected.Add(key);

        Refresh();
    }

    private void SelectAll()
    {
        var selectable = _rows.Where(r => r.Key.Length != 0).Select(r => r.Key).ToList();
        bool allSelected = selectable.Count > 0 && selectable.All(k => _selected.Contains(k));

        if (allSelected)
            _selected.Clear();
        else
            foreach (string key in selectable)
                _selected.Add(key);

        Refresh();
    }

    private void EnterDeviceFolder(string path)
    {
        _devicePath = path;
        _selected.Clear();
        SavePath();
        RememberMove();
        Refresh();
    }

    private void GoUp()
    {
        if (_mode == Mode.Container)
        {
            int cut = _containerDir.LastIndexOf('\\');
            if (cut < 0)
                LeaveContainer();
            else
                _containerDir = _containerDir[..cut];

            _selected.Clear();
            RememberMove();
            Refresh();
            return;
        }

        string? parent = Path.GetDirectoryName(_devicePath);
        if (string.IsNullOrEmpty(parent) || parent == _devicePath || IsStorageRoot(_devicePath))
        {
            SayAtRoot();
            return;
        }

        _devicePath = parent;
        _selected.Clear();
        SavePath();
        RememberMove();
        Refresh();
    }

    // Сколько секунд молчим, если «вверх» нажали на корне: несколько подряд нажатий не должны
    // превращать подпись в мелькающую строку. Пауза отсчитывается от показа, а не от каждого
    // нажатия, поэтому редкое «up-up» через десять секунд подсказку всё-таки покажет.
    private const int RootHintPauseMs = 8_000;
    private long? _rootHintShownAt;

    private void SayAtRoot()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();

        if (_rootHintShownAt is long shown)
        {
            long sinceMs = (now - shown) * 1000 / System.Diagnostics.Stopwatch.Frequency;
            if (sinceMs < RootHintPauseMs)
                return;
        }

        _rootHintShownAt = now;
        Say("Вы уже в корне общей памяти.");
    }

    /// <summary>Мы действительно переместились — значит следующая подсказка о корне законная.</summary>
    private void RememberMove() => _rootHintShownAt = null;

    /// <summary>
    /// Общая память телефона — верхняя граница: выше неё лежат только системные монтирования,
    /// показывать которые пользы нет, а ход «Назад» превращается в блуждание по /.
    /// </summary>
    private static string StorageRoot => StorageAccess.StartDirectory();

    private static bool IsStorageRoot(string path) => SamePath(path, StorageRoot);

    private static bool SamePath(string left, string right) =>
        string.Equals(left.TrimEnd('/', '\\'), right.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private static bool InsideStorage(string path) => StorageAccess.IsInsideShared(path);

    /// <summary>
    /// Аппаратная кнопка «Назад» (и жест возврата) делает то же, что строка «.. (выше)»: вверх по
    /// папкам телефона, а внутри контейнера — вверх по папкам контейнера, из папки контейнера —
    /// к списку файлов телефона. Уже на общей памяти — уходим на рабочий стол: приложение отходит
    /// на задний план, как это делает Android, а не закрывается.
    ///
    /// Android 13 и новее не вызывает OnBackPressed у приложений, нацеленных на новые версии API:
    /// возврат приходит отдельным обратным вызовом диспетчера окна. Поэтому держим оба пути —
    /// обратный вызов там, где он работает (API 33+), и OnBackPressed для всего, что старше.
    /// </summary>
    public override void OnBackPressed() => GoBack();

    private void GoBack()
    {
        if (_mode == Mode.Device && IsStorageRoot(_devicePath))
        {
            MoveTaskToBack(true);
            return;
        }

        GoUp();
    }

    private void RegisterBackHandling()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
            return;

        _backCallback = new BackCallback(this);
        OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(Android.Window.IOnBackInvokedDispatcher.PriorityDefault, _backCallback);
    }

    protected override void OnDestroy()
    {
        if (_backCallback is not null)
        {
            OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(_backCallback);
            _backCallback = null;
        }

        base.OnDestroy();
    }

    private sealed class BackCallback : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
    {
        private readonly MainActivity _owner;

        public BackCallback(MainActivity owner) => _owner = owner;

        public void OnBackInvoked() => _owner.GoBack();
    }

    private void OpenContainer(string path)
    {
        try
        {
            var (info, health) = _service.Inspect(path);
            _containerPath = path;
            _containerDir = "";
            _mode = Mode.Container;
            _selected.Clear();

            Say($"{info.FileCount} {Plural(info.FileCount, "файл", "файла", "файлов")}, {info.FolderCount} {Plural(info.FolderCount, "папка", "папки", "папок")}, {info.ChunkCount} {Plural(info.ChunkCount, "чанк", "чанка", "чанков")}, уровень zstd {info.CompressionLevel}"
                + (health.NeedsCompact ? $", мусора {SizeText(health.ReclaimableBytes)}" : ", мусора нет"));
            Refresh();
        }
        catch (Exception ex)
        {
            Say("Это не контейнер .cc: " + ex.Message);
        }
    }

    private void LeaveContainer()
    {
        _mode = Mode.Device;
        _containerDir = "";
        _selected.Clear();
        Refresh();
    }

    /// <summary>«1 запись», «2 записи», «5 записей»: счёт в подписях по-русски, а не всегда множественный.</summary>
    private static string Plural(long count, string one, string few, string many) =>
        (count % 100 is >= 11 and <= 14)
            ? many
            : (count % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };

    // ---------- действия ----------

    private void ShowActions()
    {
        string[] actions = _mode == Mode.Device
            ? [
                "Упаковать выбранное в новый .cc",
                "Добавить выбранное в открытый контейнер",
                "Свойства открытого контейнера",
                "Удалить",
              ]
            : [
                "Извлечь выбранное…",
                "Извлечь всё…",
                "Добавить файлы телефона (перейти к ним)",
                "Удалить выбранное",
                "Удалить по маске…",
                "Уплотнить контейнер (compact)",
                "Проверить выбранное",
                "Свойства контейнера",
              ];

        new AlertDialog.Builder(this)
            .SetTitle("Действия")
            .SetItems(actions, (_, e) =>
            {
                if (_mode == Mode.Device)
                    DeviceAction(actions, e.Which);
                else
                    ContainerAction(actions, e.Which);
            })
            .Show();
    }

    private void DeviceAction(string[] actions, int which)
    {
        switch (which)
        {
            case 0: PackSelected(); break;
            case 1: AddSelectedToContainer(); break;
            case 2: ShowContainerProperties(); break;
            case 3: DeleteContainerFile(); break;
            default: Say("Неизвестное действие: " + actions[Math.Clamp(which, 0, actions.Length - 1)]); break;
        }
    }

    /// <summary>
    /// «Удалить» в списке файлов телефона: контейнеры .cc — один или несколько сразу, но только
    /// когда ВСЕ отмеченные объекты контейнеры. Хотя бы один не контейнер — окно с объяснением и
    /// ничего не удаляется: половина списка удалена, половина нет, этого человек не ждал.
    /// Удаление всегда подтверждается («Вы уверены? Да / Нет»). Резервные копии .cc.bak уходят
    /// вместе со своими контейнерами: без контейнера копия ничего не защищает.
    /// Содержимое контейнера удаляется другими пунктами — «Удалить выбранное» и «Удалить по маске».
    /// </summary>
    private async void DeleteContainerFile()
    {
        List<ContainerService.Row> marked = _rows.Where(row => _selected.Contains(row.Key)).ToList();
        if (marked.Count == 0)
        {
            Say("Отметьте контейнер .cc долгим нажатием.");
            return;
        }

        List<ContainerService.Row> containers = marked.Where(row => !row.IsFolder && IsContainer(row.Key)).ToList();
        List<ContainerService.Row> notContainers = marked.Where(row => row.IsFolder || !IsContainer(row.Key)).ToList();
        if (notContainers.Count > 0)
        {
            await NoticeAsync(
                "Удалять можно только контейнеры .cc",
                $"Отмечено {marked.Count} {Plural(marked.Count, "объект", "объекта", "объектов")}, "
              + $"из них не контейнеры .cc:\n{Names(notContainers)}");
            return;
        }

        var targets = new List<(string Path, long Size, string? Backup, long BackupSize)>();
        foreach (ContainerService.Row row in containers)
        {
            string path = row.Key;
            string backup = ContainerSafety.BackupPath(path);
            bool hasBackup = File.Exists(backup);
            targets.Add((path, new FileInfo(path).Length, hasBackup ? backup : null, hasBackup ? new FileInfo(backup).Length : 0));
        }

        int copies = targets.Count(t => t.Backup is not null);
        long containerBytes = targets.Sum(t => t.Size);
        long totalBytes = containerBytes + targets.Sum(t => t.BackupSize);

        static string FileList(IEnumerable<(string Name, long Size)> items, int limit)
        {
            string head = string.Join(", ", items.Take(limit).Select(item => $"{item.Name} ({SizeText(item.Size)})"));
            return items.Count() > limit ? head + ", …" : head;
        }

        string names = FileList(targets.Select(t => (Path.GetFileName(t.Path), t.Size)), 6);
        string what = targets.Count == 1
            ? $"контейнер {names}"
            : $"{targets.Count} {Plural(targets.Count, "контейнер", "контейнера", "контейнеров")}: {names}";

        string copyNames = FileList(targets.Where(t => t.Backup is not null).Select(t => (Path.GetFileName(t.Backup!), t.BackupSize)), 6);
        string copiesText = copies switch
        {
            0 => $"\n\nОсвободится {SizeText(containerBytes)}.",
            1 => $"\n\nРядом лежит резервная копия {copyNames}. Без своего контейнера она бесполезна, поэтому удалится тоже. "
               + $"Всего освободится {SizeText(totalBytes)}.",
            _ => $"\n\nРядом лежат {copies} {Plural(copies, "резервная копия", "резервные копии", "резервных копий")}: {copyNames}. "
               + $"Без своих контейнеров они бесполезны, поэтому удалятся тоже. Всего освободится {SizeText(totalBytes)}."
        };

        string message = $"Удалить с телефона {what}?\n\nВсё, что внутри, исчезнет целиком — вернуть будет нечем.{copiesText}";

        if (!await ConfirmYesNoAsync(message))
            return;

        var failures = new List<string>();
        int removedFiles = 0;
        long freed = 0;

        foreach ((string path, long size, string? backup, long backupSize) in targets)
        {
            try
            {
                File.Delete(path);
                removedFiles++;
                freed += size;
            }
            catch (IOException ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                failures.Add($"{Path.GetFileName(path)}: нет прав — {ex.Message}");
                continue;
            }

            if (backup is null)
                continue;

            try
            {
                File.Delete(backup);
                removedFiles++;
                freed += backupSize;
            }
            catch (IOException ex)
            {
                failures.Add($"{Path.GetFileName(backup)}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                failures.Add($"{Path.GetFileName(backup)}: нет прав — {ex.Message}");
            }
        }

        _selected.Clear();
        Refresh();

        // Считаем по факту: удалённый файл больше не существует, поэтому сколько контейнеров
        // действительно ушло, видно по оставшимся.
        int removedContainers = targets.Count(t => !File.Exists(t.Path));
        int removedCopies = removedFiles - removedContainers;
        Say($"{(removedContainers == 1 ? "Удалён" : "Удалено")} {removedContainers} "
          + $"{Plural(removedContainers, "контейнер", "контейнера", "контейнеров")}"
          + (removedCopies > 0 ? $" и {removedCopies} {Plural(removedCopies, "резервная копия", "резервные копии", "резервных копий")}" : "")
          + $". Освободилось {SizeText(freed)}."
          + (failures.Count > 0 ? " Не удалось: " + string.Join("; ", failures) : ""));
    }

    private void ContainerAction(string[] actions, int which)
    {
        switch (which)
        {
            case 0: ExtractSelected(); break;
            case 1: ExtractEverything(); break;
            case 2:
                Say("Отметьте файлы или папки в списке телефона, затем «Действия → Добавить выбранное в открытый контейнер».");
                LeaveContainer();
                break;
            case 3: DeleteSelected(); break;
            case 4: DeleteByMask(); break;
            case 5: CompactContainer(); break;
            case 6: VerifySelected(); break;
            case 7: ShowContainerProperties(); break;
            default: Say("Неизвестное действие: " + actions[Math.Clamp(which, 0, actions.Length - 1)]); break;
        }
    }

    // ---------- настройки ----------

    private void ShowSettings()
    {
        string[] items =
        [
            "Фильтр по расширению: " + _settings.DeviceFilterName,
            "Тема: " + _settings.ThemeName,
            "Новый контейнер — уровень сжатия: " + _settings.CompressionLevel,
            "Новый контейнер — размер чанка: " + AppSettings.ChunkName(_settings.ChunkSize),
        ];

        new AlertDialog.Builder(this)
            .SetTitle("Настройки")
            .SetItems(items, (_, e) =>
            {
                switch (e.Which)
                {
                    case 0: ChooseDeviceFilter(); break;
                    case 1: ChooseTheme(); break;
                    case 2: ChooseLevel(); break;
                    case 3: ChooseChunkSize(); break;
                    default: Say("Неизвестная настройка: " + items[Math.Clamp(e.Which, 0, items.Length - 1)]); break;
                }
            })
            .Show();
    }

    private void ChooseDeviceFilter()
    {
        string[] names = ["все файлы", "только контейнеры .cc"];

        AlertDialog? dialog = null;
        var builder = new AlertDialog.Builder(this)
            .SetTitle("Фильтр по расширению");

        builder.SetSingleChoiceItems(names, _settings.DeviceFilter, (_, e) =>
        {
            _settings.SetDeviceFilter(e.Which);

            // В списке файлов телефона отметки сбрасываем: скрытый фильтром файл не должен тайком
            // участвовать в упаковке или добавлении. Внутри контейнера отметки остаются — там фильтр
            // ничего не прячет, и сброшенные отметки были бы просто потерей отмеченного.
            if (_mode == Mode.Device)
                _selected.Clear();

            dialog?.Dismiss();
            Refresh();
            Say(_settings.DeviceFilter == AppSettings.FilterContainers
                ? "В списке файлов телефона остались только контейнеры .cc и папки. Содержимое открытого контейнера фильтр не меняет."
                : "В списке файлов телефона снова все файлы.");
        });

        dialog = builder.Show();
    }

    private void ChooseTheme()
    {
        string[] names = ["как в системе", "светлая", "тёмная"];

        AlertDialog? dialog = null;
        var builder = new AlertDialog.Builder(this)
            .SetTitle("Тема");

        builder.SetSingleChoiceItems(names, _settings.Theme, (_, e) =>
        {
            _settings.SetTheme(e.Which);
            dialog?.Dismiss();
            Recreate();   // тема читается при создании экрана, поэтому перечитываем его
        });

        dialog = builder.Show();
    }

    private async void ChooseLevel()
    {
        string? typed = await PromptAsync(
            $"Уровень сжатия zstd для нового контейнера ({AppSettings.MinLevel}..{AppSettings.MaxLevel})",
            _settings.CompressionLevel.ToString());
        if (typed is null)
            return;

        if (!int.TryParse(typed.Trim(), out int level) || level < AppSettings.MinLevel || level > AppSettings.MaxLevel)
        {
            Say($"Уровень сжатия — целое число от {AppSettings.MinLevel} до {AppSettings.MaxLevel}.");
            return;
        }

        _settings.SetCompressionLevel(level);
        Say($"Новые контейнеры: уровень сжатия {level}. Уже созданный контейнер сохраняет свой уровень — он записан в его заголовке.");
    }

    private void ChooseChunkSize()
    {
        string[] names = AppSettings.ChunkChoices.Select(AppSettings.ChunkName).ToArray();
        int current = Array.IndexOf(AppSettings.ChunkChoices, _settings.ChunkSize);

        AlertDialog? dialog = null;
        var builder = new AlertDialog.Builder(this)
            .SetTitle("Размер чанка для нового контейнера");

        builder.SetSingleChoiceItems(names, current, (_, e) =>
        {
            _settings.SetChunkSize(AppSettings.ChunkChoices[e.Which]);
            dialog?.Dismiss();
            Say($"Новые контейнеры: чанк {AppSettings.ChunkName(_settings.ChunkSize)}. "
                + "Больший чанк быстрее упаковывает и меньше весит в индексе, меньший — выигрышнее при выборочном извлечении.");
        });

        dialog = builder.Show();
    }

    // ---------- операции ----------

    private async void PackSelected()
    {
        List<string> sources = _selected.ToList();
        if (sources.Count == 0)
        {
            Say("Отметьте файлы или папки: файл — коротким нажатием, папку и .cc — долгим.");
            return;
        }

        string suggested = Path.Combine(_devicePath, SuggestedName(sources) + ".cc");
        string? typed = await PromptAsync("Упаковать выбранное в контейнер", suggested);
        if (typed is null)
            return;

        string target = Rooted(typed);
        if (!IsContainer(target))
            target += ".cc";

        if (File.Exists(target) && !await ConfirmAsync($"Файл {Path.GetFileName(target)} уже есть. Дописать в него?", "Дописать"))
            return;

        long need = EstimateSources(sources);
        if (!await ConfirmSpaceAsync("создание контейнера", need, target))
            return;

        Run("Упаковка…", true, token =>
        {
            var report = _service.Append(target, sources, Report, () => token.IsCancellationRequested,
                _settings.CompressionLevel, _settings.ChunkSize);
            return $"Создан {Path.GetFileName(target)}: {report.AddedEntries} {Plural(report.AddedEntries, "запись", "записи", "записей")}, чанков {report.AddedChunks}, "
                 + $"чанк {AppSettings.ChunkName(_settings.ChunkSize)}, уровень {_settings.CompressionLevel}, "
                 + $"файл {SizeText(new FileInfo(target).Length)}";
        });
    }

    private async void AddSelectedToContainer()
    {
        if (_containerPath.Length == 0)
        {
            Say("Сначала откройте контейнер: нажмите на .cc-файл.");
            return;
        }

        List<string> sources = _selected.ToList();
        if (sources.Count == 0)
        {
            Say("Отметьте файлы или папки: файл — коротким нажатием, папку и .cc — долгим.");
            return;
        }

        long need = EstimateSources(sources);
        if (!await ConfirmSpaceAsync($"добавление в {Path.GetFileName(_containerPath)}", need, _containerPath))
            return;

        Run($"Добавление ({sources.Count})…", true, token =>
        {
            var report = _service.Append(_containerPath, sources, Report, () => token.IsCancellationRequested,
                _settings.CompressionLevel, _settings.ChunkSize);
            return $"Добавлено {report.AddedEntries} {Plural(report.AddedEntries, "запись", "записи", "записей")} (заменено {report.ReplacedEntries}), чанков {report.AddedChunks}, "
                 + $"дописано {SizeText(report.BytesWritten)}, контейнер {SizeText(report.ContainerSize)}"
                 + (report.BackupPath is null ? "" : ", резервная копия " + Path.GetFileName(report.BackupPath));
        });
    }

    private async void ExtractSelected()
    {
        List<string> names = SelectedEntryNames();
        if (names.Count == 0)
        {
            Say("Отметьте записи нажатием, либо возьмите «Извлечь всё».");
            return;
        }

        string? typed = await PromptAsync("Извлечь в папку", _devicePath);
        if (typed is null)
            return;

        string target = Rooted(typed);
        long need = _service.EstimatedExtractBytes(_containerPath, names);
        if (!await ConfirmSpaceAsync("извлечение", need, target))
            return;

        Run($"Извлечение ({names.Count})…", true, token =>
        {
            int done = _service.Extract(_containerPath, names, target, Report, () => token.IsCancellationRequested);
            return $"Извлечено: {done} {Plural(done, "запись", "записи", "записей")} в {target}";
        });
    }

    private async void ExtractEverything()
    {
        string? typed = await PromptAsync("Извлечь всё в папку", _devicePath);
        if (typed is null)
            return;

        string target = Rooted(typed);
        var (_, health) = _service.Inspect(_containerPath);
        if (!await ConfirmSpaceAsync("извлечение всего контейнера", health.LiveChunkBytes, target))
            return;

        // Без кнопки «Стоп»: UnpackAll читает каждый чанк один раз и остановки между файлами не делает.
        Run("Извлечение всего контейнера…", false, _ =>
        {
            _service.ExtractAll(_containerPath, target, Report);
            return $"Контейнер распакован в {target}";
        });
    }

    private async void DeleteSelected()
    {
        List<string> names = SelectedEntryNames();
        if (names.Count == 0)
        {
            Say("Отметьте записи нажатием.");
            return;
        }

        if (!await ConfirmAsync($"Удалить из контейнера отмеченное ({names.Count} {Plural(names.Count, "запись", "записи", "записей")})? Файл при этом не уменьшится — освободит место «Уплотнить».", "Удалить"))
            return;

        Run("Удаление…", false, _ =>
        {
            var report = _service.Remove(_containerPath, names);
            return $"Удалено {report.RemovedEntries} {Plural(report.RemovedEntries, "запись", "записи", "записей")}, пустых папок сохранено {report.PreservedEmptyFolders}, "
                 + $"контейнер {SizeText(report.ContainerSize)}, уплотняемого {SizeText(report.Health.ReclaimableBytes)}"
                 + (report.BackupPath is null ? "" : ", резервная копия " + Path.GetFileName(report.BackupPath));
        });
    }

    /// <summary>
    /// Удаление по маске — те же маски, что понимает плагин в Total Commander: «src\*.log»
    /// затрагивает только имена под src, «src\*.*» или «src\*» убирает папку src вместе с
    /// содержимым. Маска без разделителя относится к тому, что сейчас видно в списке.
    /// Перед удалением показывается, что именно под неё попадает.
    /// </summary>
    private async void DeleteByMask()
    {
        string? typed = await PromptAsync(
            _containerDir.Length == 0
                ? "Удалить по маске (например *.log или docs\\*.*; «*.*» — весь контейнер)"
                : $"Удалить по маске внутри {_containerDir} (например *.log или *.* — всю папку)",
            "*.*");
        if (typed is null)
            return;

        string mask = typed.Trim();
        if (mask.Length == 0)
            return;

        if (!mask.Contains('\\') && !mask.Contains('/') && _containerDir.Length > 0)
            mask = _containerDir + "\\" + mask;

        int total;
        List<string> sample;
        try
        {
            (total, sample) = _service.MatchMask(_containerPath, mask);
        }
        catch (ContainerFormatException ex)
        {
            Say(ex.Message);
            return;
        }

        if (total == 0)
        {
            Say($"Под маску «{mask}» не попала ни одна запись.");
            return;
        }

        string preview = string.Join("\n", sample);
        string more = total > sample.Count ? $"\n… и ещё {total - sample.Count}" : "";
        if (!await ConfirmAsync($"«{mask}» затрагивает {total} {Plural(total, "запись", "записи", "записей")}:\n{preview}{more}\n\n"
                + "Файл при этом не уменьшится — освободит место «Уплотнить».", "Удалить"))
            return;

        Run("Удаление…", false, _ =>
        {
            var report = _service.Remove(_containerPath, [mask]);
            return $"Удалено по маске «{mask}»: {report.RemovedEntries} {Plural(report.RemovedEntries, "запись", "записи", "записей")}, уплотняемого {SizeText(report.Health.ReclaimableBytes)}, "
                 + $"контейнер {SizeText(report.ContainerSize)} — освободит «Уплотнить»"
                 + (report.BackupPath is null ? "" : ", резервная копия " + Path.GetFileName(report.BackupPath));
        });
    }

    private async void CompactContainer()
    {
        var (_, health) = _service.Inspect(_containerPath);
        if (!health.NeedsCompact)
        {
            Say("Уплотнять нечего: мёртвых чанков и старых индексов нет.");
            return;
        }

        if (!await ConfirmAsync($"Уплотнить {Path.GetFileName(_containerPath)}? Файл будет переписан целиком: {SizeText(health.FileSize)} → ~{SizeText(health.EstimatedSizeAfterCompact)}.", "Уплотнить"))
            return;

        if (!await ConfirmSpaceAsync("уплотнение", health.EstimatedSizeAfterCompact, _containerPath))
            return;

        Run("Уплотнение…", false, _ =>
        {
            var report = _service.Compact(_containerPath);
            return report.Applied
                ? $"Уплотнение: {SizeText(report.SizeBefore)} → {SizeText(report.SizeAfter)}, освобождено {SizeText(report.Reclaimed)}, "
                  + $"сброшено чанков {report.DroppedChunks}, живых {report.LiveChunks}"
                  + (report.BackupPath is null ? "" : ", резервная копия " + Path.GetFileName(report.BackupPath))
                : $"Мусора нет: {SizeText(report.SizeBefore)} байт, файл не переписан";
        });
    }

    private void VerifySelected()
    {
        List<string> names = SelectedEntryNames();

        Run(names.Count == 0 ? "Проверка всего контейнера…" : $"Проверка ({names.Count})…", true, token =>
        {
            int done = _service.Verify(_containerPath, names, Report, () => token.IsCancellationRequested);
            return $"Проверено: {done} {Plural(done, "запись", "записи", "записей")}. Целостность подтверждена (хэш каждого чанка + декомпрессия).";
        });
    }

    private void ShowContainerProperties()
    {
        if (_containerPath.Length == 0)
        {
            Say("Открытого контейнера нет.");
            return;
        }

        try
        {
            var (info, health) = _service.Inspect(_containerPath);

            string text =
                $"{_containerPath}\n\n"
              + $"Версия формата:   {info.Version}\n"
              + $"Размер чанка:    {SizeText(info.ChunkSize)} ({info.ChunkSize:N0} байт)\n"
              + $"Длина хэша:      {info.HashLength} байт (SHA-256)\n"
              + $"Уровень zstd:    {info.CompressionLevel}\n"
              + $"Файлов:          {info.FileCount}\n"
              + $"Папок пустых:    {info.FolderCount}\n"
              + $"Чанков:          {info.ChunkCount}\n\n"
              + $"Размер файла:    {SizeText(health.FileSize)}\n"
              + $"Живые чанки:     {health.LiveChunkCount} ({SizeText(health.LiveChunkBytes)})\n"
              + $"Мёртвые чанки:   {health.DeadChunkCount} ({SizeText(health.DeadChunkBytes)})\n"
              + $"Индекс:          {SizeText(health.IndexBytes)}\n"
              + $"Прочий мусор:    {SizeText(health.GarbageBytes)}\n"
              + (health.NeedsCompact
                    ? $"После compact:   {SizeText(health.EstimatedSizeAfterCompact)} (освободится {SizeText(health.ReclaimableBytes)}, {health.GarbagePercent:F1}% файла)"
                    : "После compact:   мусора нет");

            new AlertDialog.Builder(this)
                .SetTitle("Свойства контейнера")
                .SetMessage(text)
                .SetPositiveButton("Закрыть", (_, _) => { })
                .Show();
        }
        catch (Exception ex)
        {
            Say("Не удалось прочитать контейнер: " + ex.Message);
        }
    }

    // ---------- выполнение с прогрессом и отменой ----------

    private void Run(string title, bool cancellable, Func<CancellationToken, string> work)
    {
        if (_cts is not null)
        {
            Say("Операция уже выполняется. Дождитесь или нажмите «Стоп».");
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        RunOnUiThread(() =>
        {
            _progress.Visibility = ViewStates.Visible;
            _stopButton.Visibility = cancellable ? ViewStates.Visible : ViewStates.Gone;
            _status.Text = title;
        });

        Task.Run(() =>
        {
            string result;
            try
            {
                result = work(token);
            }
            catch (System.OperationCanceledException ex)
            {
                result = "Остановлено. " + ex.Message;
            }
            catch (Exception ex)
            {
                result = ex.GetType().Name + ": " + ex.Message;
            }

            RunOnUiThread(() =>
            {
                _cts?.Dispose();
                _cts = null;
                _progress.Visibility = ViewStates.Gone;
                _stopButton.Visibility = ViewStates.Gone;
                _status.Text = result;
                Say(result);
                Refresh();
            });
        });
    }

    private void Report(string text) => RunOnUiThread(() => _status.Text = text);

    // ---------- диалоги и мелочи ----------
    //
    // Диалог асинхронный: ждать ответа на UI- потоке нельзя (это deadlock), поэтому продолжение
    // операции отдаётся наружу через await.

    private Task<string?> PromptAsync(string title, string initial)
    {
        var tcs = new TaskCompletionSource<string?>();
        var edit = new EditText(this) { Text = initial };
        edit.SetMinLines(2);
        edit.SetSelection(edit.Text.Length);

        ShowDialog(new AlertDialog.Builder(this)
            .SetTitle(title)
            .SetView(edit)
            .SetCancelable(false)
            .SetPositiveButton("ОК", (_, _) => tcs.TrySetResult(edit.Text))
            .SetNegativeButton("Отмена", (_, _) => tcs.TrySetResult(null))
            .Create());

        return tcs.Task;
    }

    /// <summary>Одно окно с объяснением и кнопкой «Понятно»: действие невозможно — и это нужно сказать, а не сделать вид, что всё в порядке.</summary>
    private Task NoticeAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();

        ShowDialog(new AlertDialog.Builder(this)
            .SetTitle(title)
            .SetMessage(message)
            .SetCancelable(false)
            .SetPositiveButton("Понятно", (_, _) => tcs.TrySetResult(true))
            .Create());

        return tcs.Task;
    }

    private Task<bool> ConfirmAsync(string message, string okLabel)
    {
        var tcs = new TaskCompletionSource<bool>();

        ShowDialog(new AlertDialog.Builder(this)
            .SetMessage(message)
            .SetCancelable(false)
            .SetPositiveButton(okLabel, (_, _) => tcs.TrySetResult(true))
            .SetNegativeButton("Отмена", (_, _) => tcs.TrySetResult(false))
            .Create());

        return tcs.Task;
    }

    /// <summary>Подтверждение удаления: именно «Вы уверены?» с кнопками «Да» и «Нет».</summary>
    private Task<bool> ConfirmYesNoAsync(string message)
    {
        var tcs = new TaskCompletionSource<bool>();

        ShowDialog(new AlertDialog.Builder(this)
            .SetTitle("Вы уверены?")
            .SetMessage(message)
            .SetCancelable(false)
            .SetPositiveButton("Да", (_, _) => tcs.TrySetResult(true))
            .SetNegativeButton("Нет", (_, _) => tcs.TrySetResult(false))
            .Create());

        return tcs.Task;
    }

    // Android: AlertDialog.BUTTON_POSITIVE, BUTTON_NEGATIVE, BUTTON_NEUTRAL.
    private const int ButtonPositive = -1;
    private const int ButtonNegative = -2;
    private const int ButtonNeutral = -3;

    /// <summary>Показать диалог и вернуть кнопки к обычному написанию: тема Android пишет их заглавными («ДА», «ОТМЕНА»), а нужны «Да», «Отмена».</summary>
    private static void ShowDialog(AlertDialog dialog)
    {
        dialog.Show();

        foreach (int which in new[] { ButtonPositive, ButtonNegative, ButtonNeutral })
            dialog.GetButton(which)?.SetAllCaps(false);
    }

    /// <summary>
    /// Сколько займёт операция и есть ли столько места. Операции контейнера не умеют
    /// «дописывать по мере надобности», поэтому нехватка места посреди работы — испорченная
    /// распаковка, а не предупреждение.
    /// </summary>
    private async Task<bool> ConfirmSpaceAsync(string what, long needed, string where)
    {
        long free = StorageAccess.AvailableBytes(where);
        if (free < 0 || needed <= 0 || free > needed)
            return true;

        Say($"Свободно {SizeText(free)}, а для «{what}» нужно около {SizeText(needed)}.");
        return await ConfirmAsync(
            $"На разделе осталось {SizeText(free)}, а «{what}» потребует около {SizeText(needed)}. Продолжить всё равно?",
            "Продолжить");
    }

    /// <summary>
    /// Перечень объектов в сообщении: каждый с новой строки, папки с косой чертой.
    /// Больше десяти — на одиннадцатом месте многоточие, чтобы окно оставалось читаемым.
    /// </summary>
    private static string Names(List<ContainerService.Row> rows, int limit = 10)
    {
        string head = string.Join("\n", rows.Take(limit).Select(row => row.IsFolder && !row.Name.EndsWith("/") ? row.Name + "/" : row.Name));
        return rows.Count > limit ? head + "\n..." : head;
    }

    private List<string> SelectedEntryNames()
    {
        var names = new List<string>();
        foreach (ContainerService.Row row in _rows)
            if (row.Key.Length != 0 && _selected.Contains(row.Key))
                names.Add(row.Key);
        return names;
    }

    private static long EstimateSources(IReadOnlyList<string> paths)
    {
        long total = 0;
        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    total += new FileInfo(path).Length;
                }
                else if (Directory.Exists(path))
                {
                    foreach (string file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                        total += new FileInfo(file).Length;
                }
            }
            catch (IOException)
            {
                // Оценка не должна срывать операцию: проверка свободного места просто станет мягче.
            }
        }
        return total;
    }

    private string SuggestedName(IReadOnlyList<string> sources)
    {
        if (sources.Count == 1)
        {
            string name = Path.GetFileName(sources[0].TrimEnd('/', '\\'));
            if (name.Length > 0)
                return name;
        }

        string leaf = Path.GetFileName(_devicePath.TrimEnd('/', '\\'));
        return leaf.Length > 0 ? leaf : "container";
    }

    private static string Rooted(string path)
    {
        path = path.Trim().Trim('"');
        if (Path.IsPathRooted(path))
            return path;
        return Path.Combine(StorageAccess.StartDirectory(), path);
    }

    private void Say(string text)
    {
        RunOnUiThread(() => _status.Text = text);
        Toast.MakeText(this, text, ToastLength.Long)?.Show();
    }

    private static string SizeText(long bytes)
    {
        if (bytes < 1024)
            return bytes + " байт";
        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " КБ";
        if (bytes < 1024L * 1024 * 1024)
            return (bytes / (1024.0 * 1024)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " МБ";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " ГБ";
    }

    private void SavePath()
    {
        try { GetSharedPreferences(PrefsName, FileCreationMode.Private).Edit().PutString(PrefsPathKey, _devicePath).Apply(); }
        catch (Exception) { /* путь всего лишь запоминается: терять из-за этого операцию нельзя */ }
    }

    private string RestorePath()
    {
        try { return GetSharedPreferences(PrefsName, FileCreationMode.Private).GetString(PrefsPathKey, "") ?? ""; }
        catch (Exception) { return ""; }
    }
}
