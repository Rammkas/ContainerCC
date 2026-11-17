using Android.Content;
using Container_V3;

namespace ContainerCC.Mobile;

/// <summary>
/// Настройки приложения: тема и параметры создаваемого контейнера.
/// Лежат в тех же SharedPreferences, что и последняя открытая папка.
///
/// Уровень сжатия и размер чанка влияют только на новые контейнеры: у готового контейнера они
/// записаны в его заголовке, и добавление в него берёт их оттуда — иначе новые чанки оказались бы
/// сжатых не так, как обещает заголовок.
/// </summary>
public sealed class AppSettings
{
    private const string PrefsName = "containercc";
    private const string ThemeKey = "theme";
    private const string LevelKey = "new_level";
    private const string ChunkKey = "new_chunk";
    private const string FilterKey = "device_filter";

    public const int ThemeSystem = 0;
    public const int ThemeLight = 1;
    public const int ThemeDark = 2;

    /// <summary>Список файлов телефона: показывать всё или только контейнеры .cc (папки остаются всегда).</summary>
    public const int FilterAll = 0;
    public const int FilterContainers = 1;

    public const int MinLevel = 1;
    public const int MaxLevel = 22;

    /// <summary>Размеры чанка, между которыми можно выбирать: от 64 КБ до 16 МБ — границы, которые допускает формат.</summary>
    public static readonly int[] ChunkChoices =
    [
        64 * 1024, 128 * 1024, 256 * 1024, 512 * 1024,
        1024 * 1024, 2 * 1024 * 1024, 4 * 1024 * 1024, 8 * 1024 * 1024, 16 * 1024 * 1024
    ];

    private readonly Context _context;

    public AppSettings(Context context) => _context = context;

    private ISharedPreferences Prefs => _context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;

    /// <summary>Edit() в Android помечен как способный вернуть null; без проверки каждое сохранение давало бы предупреждение компилятора.</summary>
    private void PutInt(string key, int value)
    {
        ISharedPreferencesEditor? editor = Prefs.Edit();
        if (editor is null)
            return;
        editor.PutInt(key, value).Apply();
    }

    public int Theme => Prefs.GetInt(ThemeKey, ThemeSystem);

    public void SetTheme(int value) => PutInt(ThemeKey, value);

    public string ThemeName => Theme switch
    {
        ThemeLight => "светлая",
        ThemeDark => "тёмная",
        _ => "как в системе"
    };

    /// <summary>Ресурс темы для SetTheme.</summary>
    public int ThemeResource => Theme switch
    {
        ThemeLight => Resource.Style.Theme_ContainerCC_Light,
        ThemeDark => Resource.Style.Theme_ContainerCC_Dark,
        _ => Resource.Style.Theme_ContainerCC_System
    };

    /// <summary>
    /// Что показывать в списке файлов телефона: всё или только контейнеры .cc. Папки остаются
    /// всегда — иначе некуда идти. На содержимое уже открытого контейнера не влияет.
    /// </summary>
    public int DeviceFilter => Prefs.GetInt(FilterKey, FilterAll) == FilterContainers ? FilterContainers : FilterAll;

    public void SetDeviceFilter(int value) => PutInt(FilterKey, value == FilterContainers ? FilterContainers : FilterAll);

    public string DeviceFilterName => DeviceFilter == FilterContainers ? "только контейнеры .cc" : "все файлы";

    public int CompressionLevel => Math.Clamp(Prefs.GetInt(LevelKey, ContainerFormat.DefaultCompressionLevel), MinLevel, MaxLevel);
    public void SetCompressionLevel(int value) => PutInt(LevelKey, Math.Clamp(value, MinLevel, MaxLevel));

    public int ChunkSize => NearestChoice(Prefs.GetInt(ChunkKey, ContainerFormat.DefaultChunkSize));

    public void SetChunkSize(int value) => PutInt(ChunkKey, NearestChoice(value));

    public static string ChunkName(int bytes) =>
        bytes % (1024 * 1024) == 0 ? bytes / (1024 * 1024) + " МБ" : bytes / 1024 + " КБ";

    /// <summary>Значение вне списка притягивается к ближайшему разрешённому — формат другие не принимает.</summary>
    private static int NearestChoice(int value)
    {
        int best = ChunkChoices[0];
        foreach (int choice in ChunkChoices)
            if (Math.Abs(choice - value) < Math.Abs(best - value))
                best = choice;
        return best;
    }
}
