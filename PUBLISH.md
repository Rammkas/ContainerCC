# Как собрать переносимую версию Container_V3

Раньше в `bin\Release\net10.0-windows\` лежало 8 файлов (`exe`, `dll`, `deps.json`,
`runtimeconfig.json`, `ZstdSharp.dll`, `System.IO.Hashing.dll`, `.pdb`), и при переносе
приходилось угадывать, что из этого нужно копировать. Теперь есть профили публикации —
результат собирается в отдельную папку `publish\`, и весь переносимый артефакт — это
одна папка (или вообще один файл).

Исходный код (`Program.cs`, упаковщик/распаковщик, `ZstdSharp`) менять не пришлось:
программа не зависит от своего расположения, работают относительные пути. Изменены только
файлы сборки.

## Что добавлено

| Файл | Зачем |
|---|---|
| `Container_V3/Properties/PublishProfiles/single-file.pubxml` | профиль «один exe» |
| `Container_V3/Properties/PublishProfiles/framework-dependent.pubxml` | профиль «компактная папка» |
| `Container_V3/Container_V3.csproj` | общие настройки публикации + `.pdb` больше не копируется в публикацию |
| `publish.ps1` (в корне) | одна команда вместо длинного `dotnet publish` |

## Вариант 1 — один exe (рекомендуется)

```powershell
.\publish.ps1                     # то же самое, что -Profile single-file
```

или напрямую:

```powershell
dotnet publish Container_V3\Container_V3.csproj -c Release -p:PublishProfile=single-file
```

Результат: `publish\single-file\Container_V3.exe`, **36 МБ, один файл**.
Внутри него — .NET рантайм 10 и все зависимости (`ZstdSharp`, `System.IO.Hashing`).
На целевом компьютере **не надо ничего устанавливать**: скопировали exe — работает, хоть с
флешки. Проверено: `selftest` проходит, упаковка/распаковка из случайной папки даёт
совпадающие SHA-256 хэши.

Минусы: файл большой; первая сборка чуть дольше (нужен пакет рантайма `win-x64`);
exe привязан к архитектуре (для ARM — `-Runtime win-arm64`).

## Вариант 2 — exe + библиотека (компактно)

```powershell
.\publish.ps1 -Profile framework-dependent
```

Результат: `publish\framework-dependent\`, **0,67 МБ, 6 файлов**:

```
Container_V3.exe              158 КБ   <- запускаемый файл
Container_V3.dll               87 КБ   <- сама программа
ZstdSharp.dll                 387 КБ   <- zstd-сжатие (чистый C#)
System.IO.Hashing.dll          55 КБ   <- SHA-256
Container_V3.deps.json        1,8 КБ
Container_V3.runtimeconfig.json 0,4 КБ
```

Копируете **всю папку целиком** — гадать больше не нужно. Минус: на целевом ПК должен
быть установлен .NET Runtime 10 (`Microsoft.NETCore.App` 10.x; Desktop-версия не нужна,
это консольное приложение).

Оба варианта: `.\publish.ps1 -Profile both`.

## Проверка собранной версии

```powershell
.\publish\single-file\Container_V3.exe selftest
.\publish\single-file\Container_V3.exe pack C:\Data\Project -o C:\Out\project.cc -chunk 256K
.\publish\single-file\Container_V3.exe list C:\Out\project.cc
.\publish\single-file\Container_V3.exe unpack-all C:\Out\project.cc C:\Restored
```

## Замечания

- Профили подключает сам SDK через `-p:PublishProfile=<имя>`; имена профилей — имена
  файлов `.pubxml`.
- `PublishDir` задан внутри каждого профиля, поэтому публикация не трогает `bin\` и не
  мешает обычной сборке из Visual Studio.
- `.pdb` в папку публикации больше не копируется (target `RemovePdbsFromPublish`), отладка
  в `bin\` осталась как была.
- Ужать однофайловый exe обрезкой (trimming) сейчас не выходит: `NETSDK1124`, потому что
  `ZstdSharp.csproj` многотаргетный (в списке есть `netstandard2.x`). Для trim нужно
  либо оставить у `ZstdSharp` один `TargetFrameworks` (`net9.0`), либо взять готовый
  пакет `ZstdSharp.Port` из NuGet вместо `ProjectReference` — тогда exe станет ~12 МБ.
- Вариант «exe + папка `libs` с dll рядом» в .NET штатно не делается: хост приложения
  ищет зависимости в папке рядом с exe. Если очень хочется разделить, единственный
  надёжный путь — вариант 1 (один файл) или вариант 2 (всё в одной папке).

## Плагин Total Commander и архив для пересылки

```powershell
dotnet publish ContainerWcx\ContainerWcx.csproj -c Release -r win-x64 --nologo   # -> publish\wcx-plugin\ContainerCC.dll
pwsh -NoProfile -File tools\verify-plugin.ps1 -Compare                          # харнесс: 577 проверок именно на этом файле
pwsh -NoProfile -File tools\make-release.ps1                                    # -> dist\ContainerCC\ и dist\ContainerCC-<версия>-win64.zip
```

- В архив попадают: `ContainerCC.wcx64` (ровно тот файл, что проверял харнесс: NativeAOT при
  пересборке выдаёт другие байты, поэтому после проверки плагин лучше не пересобирать), установщик
  `install-tc-plugin.ps1` (сам находит каталог Total Commander и её `Wincmd.ini`, перед правкой
  делает резервную копию ini в `ini-backups`), `install.bat` и `uninstall.bat` (запуск двойным
  щелчком, работают и в Windows PowerShell 5.1), `README.md` для получателя и `SHA256.txt`.
- Получателю не нужен .NET: плагин — один нативный файл; требуется 64-битная Total Commander.
- Флаг `-WithCli` кладёт в архив `Container_V3.exe` одним файлом (он нужен для команды `compact`),
  `-NoZip` собирает папку без архива, `-Version` меняет версию в имени архива.
- Проверка без Total Commander: `pwsh -NoProfile -File tools\verify-plugin.ps1 -SkipBuild -Compare`.
- Подробно об установке и работе с контейнером: `INSTALL-TC.md`.
