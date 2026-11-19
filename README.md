# Container V3

Формат `.cc` — однофайловый контейнер (аналог архива) с дописыванием вместо полной
переупаковки: файлы добавляются в конец, индекс переписывается, мёртвые чанки собирает
`compact`. Данные сжимаются Zstd (vendored `ZstdSharp`), целостность — SHA-256.

Репозиторий содержит сам формат, его реализацию на C#/.NET и три оболочки: командную
строку, плагин WCX для Total Commander и приложение для Android.

| Версия формата | 1.3.1 |
|---|---|
| Целевая платформа | .NET 10 (`net10.0` / `net10.0-windows`), только x64 для плагина |
| Формат файла | V3, обратная совместимость с V1/V2 не предусмотрена (см. `Container_V3/ContainerFormat.md`) |

## Из чего состоит

| Проект | Что это |
|---|---|
| `Container.Core` | ядро формата: упаковка, дописывание, удаление, извлечение, хэши, раскладка файлов |
| `Container_V3` | консольная программа (`pack`, `add`, `delete`, `compact`, `info`, `list`, `unpack-all`, `unpack-file`, `unpack-folder`, `pack-bench`, `selftest`) |
| `ContainerWcx` | 64-битный плагин WCX для Total Commander, NativeAOT |
| `ContainerCC.Android` | приложение для Android (в решении, но по умолчанию не собирается) |
| `ZstdSharp` | vendored Zstd-декомпрессор/компрессор |
| `tools/wcx-harness` | верификационный харнесс плагина: прогоняет проверки по собранному `.dll`-плагину и печатает machine-readable TSV |
| `Container.Core.Tests`, `Container.Cli.Tests`, `ContainerWcx.Tests` | xUnit-тесты (161 тест) |

## Сборка и тесты

```powershell
dotnet build Container_V3.sln -c Release
dotnet test Container_V3.sln -c Release
```

Тесты `ContainerWcx.Tests` делятся на две группы:

```powershell
# лёгкие: раскладка, разбор аргументов харнесса — плагин и MSVC не нужны
dotnet test ContainerWcx.Tests\ContainerWcx.Tests.csproj -c Release --filter "Category!=WcxNative"

# тяжёлые: NativeAOT-плагин и полный прогон харнесса (нужен MSVC)
dotnet test ContainerWcx.Tests\ContainerWcx.Tests.csproj -c Release -p:Platform=x64 --filter "Category=WcxNative"
```

Ключ фильтра — `Category`: адаптер VSTest кладёт `[Trait("Category", ...)]` в свойство
`Category`, а `TestCategory` к трейтам xUnit не применяется.

Переменные среды для тестов плагина: `WCX_REBUILD=1` (пересобрать плагин),
`WCX_BUILD_PLUGIN=0` (запретить сборку, использовать готовый `publish\wcx-plugin`),
`WCX_HARNESS_PLATFORM=x64` (платформа, которую фикстуар передаёт своим `dotnet build`
и `dotnet publish`; нужен, когда хост тестов не получает `Platform` из окружения),
`WCX_GUI=1` (разрешить проход 14.1 — диалог «Настроить» требует интерактивного рабочего
стола, поэтому в CI он не запускается).

`Container_V3.exe selftest` — ручная проверка: она не входит в xUnit-набор и в CI.

## Документация

- `Container_V3/ContainerFormat.md` — описание формата `.cc`: заголовок, чанки, индекс, отличия от V2.
- `INSTALL-TC.md` — установка и возможности плагина в Total Commander.
- `PUBLISH.md` — профили публикации переносимой версии (`publish.ps1`).
- `.github/workflows/ci.yml` — два job'а на `windows-latest`: быстрые тесты и полный прогон харнесса.
