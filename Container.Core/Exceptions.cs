namespace Container_V3;

/// <summary>
/// Ошибки формата и целостности контейнера. Отдельно от ошибок файловой системы.
/// inner нужен затем, что изменение контейнера превывает и системные ошибки (файл занят,
/// не хватило места) в сообщение о том, что именно не удалось сделать.
/// </summary>
public class ContainerFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Файл на диске не совпал с манифестом, снятым при сканировании (изменился/исчез).</summary>
public sealed class ManifestMismatchException(string path, long expected, long actual)
    : Exception($"'{path}': ожидалось {expected} байт, по факту {actual}. Файл изменился между сканированием и упаковкой.");

/// <summary>Данные чанка не совпали с хэшем, записанным при упаковке.</summary>
public sealed class ChunkIntegrityException(int chunkNumber)
    : Exception($"Чанк #{chunkNumber}: сохранённый хэш не совпал с вычисленным. Данные повреждены.");

/// <summary>
/// Ничего из перечисленного в контейнере нет: сам контейнер при этом в порядке. Это ответ на
/// запрос, а не повреждение, и различие не косметическое — Total Commander показывает их разными
/// кодами (E_NO_FILES против E_BAD_ARCHIVE), а CLI печатает то и другое как ошибку формата.
/// </summary>
public sealed class NothingToRemoveException(string message) : ContainerFormatException(message);

/// <summary>
/// Упаковку остановил тот, кто её вызвал: в Total Commander это Stop в диалоге прогресса.
/// Насколько он внезапен, настолько же безопасен — индекса не тронули, значит контейнер читабелен
/// ровно в том состоянии, в котором был до операции; дописанные чанки стали мусором и уйдут при compact.
/// </summary>
public sealed class PackingAbortedException(string message) : ContainerFormatException(message);

/// <summary>Собранный файл короче/длиннее, чем обещано в индексе.</summary>
public sealed class ReassemblyException(string path, long expected, int actual, Exception? inner = null)
    : Exception($"'{path}': собрано {actual} байт, ожидалось {expected}.", inner);
