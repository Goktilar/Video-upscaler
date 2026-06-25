# Video Upscaler App

Приложение для апскейлинга (масштабирования) видео с использованием Streamlit, OpenCV и MoviePy.

## Возможности
- Апскейл видео в 1.5x, 2x, 3x, 4x.
- Выбор методов интерполяции (Lanczos, Bicubic, Nearest Neighbor, а также **AI Super Resolution**).
- Поддержка русского и английского языков.
- Светлая и темная темы оформления.
- Сохранение аудио при обработке.

## Как запустить

### 1. Установка зависимостей
Рекомендуется использовать виртуальное окружение.
```bash
pip install -r requirements.txt
```

### 2. Загрузка моделей ИИ
Для использования функций ИИ необходимо скачать предобученные модели в папку `models/`.
Например, для EDSR x2:
```bash
mkdir -p models
curl -L https://github.com/Saafke/EDSR_Tensorflow/raw/master/models/EDSR_x2.pb -o models/EDSR_x2.pb
```

### 3. Запуск приложения
```bash
streamlit run app.py
```

## Как "скомпилировать" (создать исполняемый файл)

### Вариант 1: Python (Streamlit)
Приложения на Streamlit обычно развертываются как веб-сервисы, но если вам нужен исполняемый файл (.exe), вы можете использовать **PyInstaller** или **StickyTape**, однако для Streamlit это может быть сложно.

Более надежные способы:
1. **Docker**: Создайте контейнер с приложением.
2. **Streamlit Sharing / Hugging Face Spaces**: Разверните приложение в облаке для общего доступа.
3. **PyInstaller + хуки**: Существуют специальные шаблоны (например, `streamlit-embedder`) для упаковки Streamlit в один файл.

Инструкция по упаковке через PyInstaller (упрощенно):
```bash
pip install pyinstaller
pyinstaller --onefile --additional-hooks-dir=. app.py
```
*Примечание: для корректной работы Streamlit внутри PyInstaller требуются дополнительные настройки путей к ресурсам.*

### Вариант 2: C# / Visual Studio (Windows)
В репозитории создана папка `VideoUpscalerVS/`, содержащая проект .NET (WPF), который можно открыть и скомпилировать напрямую в Visual Studio.

1. Откройте Visual Studio.
2. Выберите "Open a project or solution" и укажите путь к `VideoUpscalerVS/VideoUpscalerVS.csproj`.
3. Все зависимости (OpenCvSharp4) подгрузятся автоматически через NuGet.
4. Нажмите **Build > Build Solution** или `F5` для запуска.
5. Для публикации в один .exe: **Right Click Project > Publish... > Folder > Single File**.

*Требуется установленная библиотека OpenCV или соответствующие NuGet-пакеты рантаймов (включены в проект).*
