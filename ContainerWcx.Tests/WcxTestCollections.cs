namespace Container_V3.WcxTests;

/// <summary>
/// Проверки раскладки и разбора аргументов делят один фикстуар: он собирает харнесс, тестовое
/// дерево и контейнер, но не трогает NativeAOT-плагин. Collection (а не IClassFixture) выбран
/// потому, что иначе каждый тестовый класс собирал бы харнесс и упаковывал дерево заново.
/// </summary>
[CollectionDefinition(WcxLayoutCollection.Name)]
public sealed class WcxLayoutCollection : ICollectionFixture<WcxLayoutFixture>
{
    public const string Name = "wcx-layout";
}

/// <summary>
/// Тесты, которым нужен собранный NativeAOT-плагин и полный прогон харнесса: один дорогой
/// фикстуар на оба класса. Отфильтровываются на CI без Visual Studio трейтом Category=WcxNative.
/// </summary>
[CollectionDefinition(WcxNativeCollection.Name)]
public sealed class WcxNativeCollection : ICollectionFixture<WcxHarnessFixture>
{
    public const string Name = "wcx-native";
}
