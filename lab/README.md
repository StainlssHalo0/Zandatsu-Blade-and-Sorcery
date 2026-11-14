# Тестовая площадка ZandatsuLab Grid

Отдельная карта для PCVR. Ровный пол с метровыми клетками помогает видеть положение и размеры кусков. Однотонные стены и нейтральный свет упрощают сравнение кадров.

После установки Lab карту можно открыть командой `loadlevel ZandatsuLabGrid` в консоли игры. Для автоматического прогона без физического шлема используй существующий запуск Lab через Steam:

```powershell
& 'C:\Program Files (x86)\Steam\steam.exe' -applaunch 629730 -vrmode mockhmd -level ZandatsuLabGrid -skipintro -skipmodswarning -zandatsulab swing_basic,armour,piece_light
```

Скриншоты, `summary.txt` и `results.json` сохраняются в `%USERPROFILE%\AppData\LocalLow\Warpfrog\BladeAndSorcery\ZandatsuLab`. Файл `last.txt` указывает на последний прогон. Для проверки игровых световых объёмов используй карту с такими объёмами, например `DungeonOutpost` для сценария `prop_light`.

Для разрывов передай `-zandatsulab rip_tear,rip_grab,rip_tk`. Эти сценарии проверяют физический разрыв сустава, повторный разрез конечности и перенос хвата при двух захватах рукой или телекинезом. Виртуальные руки управляются стендом.

Готовые файлы карты находятся в `map/PC/`. Сборка `ZandatsuLab.csproj` с `-p:DeployToGame=true` копирует их в `Mods/ZandatsuLab`.

Для пересборки карты скопируй `map/Editor/ZandatsuLabGridBuilder.cs` в `Assets/ZandatsuLabGrid/Editor/` локального BasSDK. Запусти Unity редактора SDK с параметрами `-batchmode -nographics -quit -projectPath <путь к BasSDK> -buildTarget Win64 -executeMethod ZandatsuLabGridBuilder.Build -zandatsuLabOut <путь к lab/map/PC>`.
