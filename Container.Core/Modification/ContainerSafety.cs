namespace Container_V3.Modification;

/// <summary>
/// Резервная копия контейнера перед изменением.
///
/// Правило договорённости: копия делается только для контейнеров до 100 МБ включительно.
/// Копировать 10 ГБ ради страховки бессмысленно — они не помещаются ни на флешку, ни в
/// то же свободное место, а время операции вырастает в разы. Для больших контейнеров
/// страховкой служит сам порядок записи: старые данные не трогаются, а новым считается
/// только то, на что укажет исправленный заголовок (см. ContainerModifier).
///
/// Хранится одна копия — <контейнер>.cc.bak, самая свежая. Иначе папка с контейнерами
/// удваивалась бы на каждую операцию.
/// </summary>
public static class ContainerSafety
{
    /// <summary>Порог, выше которого резервную копию не делать.</summary>
    public const long BackupThresholdBytes = 100L * 1024 * 1024;

    public const string BackupSuffix = ".bak";

    public static string BackupPath(string containerPath) => containerPath + BackupSuffix;

    public static bool IsBackup(string path) => path.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Вернуть путь созданной копии либо null, если копия не делалась (контейнер больше порога).
    /// Делать копию нужно до того, как открыт файл на запись: File.Copy открывает источник
    /// с FileShare.Read, а такой доступ несовместим с уже открытым на запись хэндлом.
    /// </summary>
    public static string? EnsureBackup(string containerPath)
    {
        var info = new FileInfo(containerPath);
        if (!info.Exists)
            throw new FileNotFoundException($"Контейнер не найден: {containerPath}", containerPath);

        if (info.Length > BackupThresholdBytes)
            return null;

        string backup = BackupPath(containerPath);

        if (string.Equals(backup, containerPath, StringComparison.OrdinalIgnoreCase))
            throw new ContainerFormatException($"Резервная копия совпадает с самим контейнером: {backup}.");

        File.Copy(containerPath, backup, overwrite: true);
        return backup;
    }
}
