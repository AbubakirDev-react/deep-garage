# Deep Garage

Иммерсивный 3D-сайт: камера плывёт сквозь тёмное пространство от машины к машине.

Сейчас на сайте 4 машины с настоящими 3D-моделями: Porsche 911 Carbon, Lamborghini Centenario Roadster, Bugatti Chiron Mansory Centuria и Rolls-Royce Ghost. Данные ещё 14 машин (BMW и другие) хранятся в `index.html` в массиве `ALL_CARS`. Чтобы машина появилась на сайте, добавьте её `id` в `LINEUP` и запись с моделью в `models/manifest.json`.

## Запуск локально

Модели грузятся через `fetch`, поэтому по `file://` страница не работает. Нужен локальный сервер:

```bash
powershell -ExecutionPolicy Bypass -File serve.ps1
```

Затем откройте http://localhost:8080.

## Состав

| Файл | Назначение |
|---|---|
| `index.html` | Весь сайт: CSS и JS внутри. Three.js r128 (cdnjs), GLTFLoader 0.128.0 (jsdelivr), без Draco |
| `models/manifest.json` | Модели: автор, лицензия, ориентация, сопоставление материалов |
| `models/*.glb` | Сжатые модели |
| `tools/optimize-glb.ps1` + `GlbOptimizer.cs` | Сжатие .glb без Node: удаление лишнего, упрощение сетки, текстуры до 1024 px |
| `serve.ps1` | Локальный сервер |

## 3D-модели

| Машина | Модель | Автор | Лицензия | Размер | Треугольники |
|---|---|---|---|---|---|
| Porsche 911 Carbon | [(FREE) Porsche 911 Carrera 4S](https://sketchfab.com/3d-models/free-porsche-911-carrera-4s-d01b254483794de3819786d93e0e1ebf) | [Karol Miklas](https://sketchfab.com/karolmiklas) | [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/) | 4,7 МБ (было 24,2) | 128 574 (было 652 660) |
| Lamborghini Centenario Roadster | [Lamborghini Centenario Roadster SDC](https://sketchfab.com/3d-models/lamborghini-centenario-roadster-sdc-938d3e3642cf4ea895c62c2a4bf6fc08) | [SDC PERFORMANCE](https://sketchfab.com/Lambo_SC04) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) | 8,4 МБ (было 33,3) | 144 833 (было 352 095) |
| Bugatti Chiron Mansory Centuria | [Bugatti Chiron Mansory](https://sketchfab.com/3d-models/bugatti-chiron-mansory-1bad4302097e41dc9b9e04794dd20108) | [VTX](https://sketchfab.com/VTX_car) | [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/), только некоммерческое использование | 9,3 МБ (было 31,1) | 140 305 (было 361 897) |
| Rolls-Royce Ghost | [Rolls Royce Ghost](https://sketchfab.com/3d-models/rolls-royce-ghost-1f614e803fc24946b83bdc2dd9926d62) | [kurojishi](https://sketchfab.com/kurojishi) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) | 5,9 МБ (было 15,1) | 133 358 (было 256 284) |

Все модели изменены: сетка упрощена, текстуры уменьшены до 1024 px, материалы переопределены, анимация и скелет удалены. Подробности по каждой модели — в `models/manifest.json` и в таблице «Все модели» на сайте.

Модель Bugatti распространяется по некоммерческой лицензии, поэтому сайт нельзя монетизировать, пока она в составе. Изменённые модели под CC BY-SA и CC BY-NC-SA распространяются под теми же лицензиями.

## Как добавить модель

1. Скачайте .glb с Sketchfab. Лицензия видна на странице модели и в `asset.extras` внутри файла.
2. Сожмите:
   ```bash
   powershell -ExecutionPolicy Bypass -File tools\optimize-glb.ps1 -In "C:\Users\K1nbo\Downloads\car.glb" -Out models\car-id.glb -Tris 120000
   ```
   Цель — меньше 150 тыс. треугольников и 10 МБ. Лишнее убирается параметрами `-DropMaterials "имя1,имя2"` и `-DropNodes "regex"`.
3. Добавьте запись в `models/manifest.json` и `id` машины в `LINEUP` в `index.html`.
4. Проверьте ориентацию в консоли браузера: `__deepGarage.view(i, 'front')`. Перед машины должен смотреть на камеру. Если нет, задайте `front` в манифесте.

## Чек-лист

| Пункт | Статус |
|---|---|
| Ориентация всех 4 моделей: перед +X, колёса на y=0 | ✅ проверено видами в браузере и по положению фар и фонарей |
| Модели ≤ 10 МБ и ≤ 150 тыс. треугольников | ✅ |
| Общий вес | ≈ 28,4 МБ моделей + 0,1 МБ страница + ≈ 0,7 МБ Three.js с CDN. Сразу грузятся только текущая и соседняя машины, остальные — в фоне |
| Ширина 400 px без горизонтальной прокрутки | ✅ |
| FPS на ноутбуке и телефоне | ⚠️ не измерено: встроенный браузер, в котором я проверял, тормозит отрисовку. При FPS ниже 38 разрешение снижается автоматически |
| Технические данные машин | ⚠️ приблизительные, сверить с сайтами брендов перед публикацией |
