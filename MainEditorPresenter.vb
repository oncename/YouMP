' Path: MainEditorPresenter.vb
Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks
Imports Serilog
Imports yoump.IServices

Public Structure ExportProgressData
    Public ReadOnly Percentage As Integer
    Public ReadOnly TimeRemaining As String

    Public Sub New(percentage As Integer, timeRemaining As String)
        Me.Percentage = percentage
        Me.TimeRemaining = timeRemaining
    End Sub
End Structure

Public Class ExportOptions
    Public Property SourceFile As String
    Public Property SelectedFormat As String
    Public Property VideoEncoder As String
    Public Property CompressionLevel As String
    Public Property TargetWidth As Integer
    Public Property TargetHeight As Integer
    Public Property CropX As Integer
    Public Property CropY As Integer
    Public Property CropW As Integer
    Public Property CropH As Integer
    Public Property InputHasImage As Boolean
    Public Property ExtractTime As TimeSpan
    Public Property IsNvidiaGpuSelected As Boolean
    Public Property IsAmdGpuSelected As Boolean
    Public Property IsAudioReplaced As Boolean
    Public Property ExternalAudioPath As String
    Public Property AudioOffset As TimeSpan
    Public Property TrackVolume As Single
    Public Property AudioFadeIn As TimeSpan
    Public Property AudioFadeOut As TimeSpan
    Public Property VideoFadeIn As TimeSpan
    Public Property VideoFadeOut As TimeSpan
    Public Property SourceFps As Double
End Class

Public Class ExportService
    Private ReadOnly _ffmpegService As FFmpegService
    Private ReadOnly _fileManager As FileManager
    Private ReadOnly _model As ProjectModel

    Public Sub New(ffmpeg As FFmpegService, fileManager As FileManager, model As ProjectModel)
        _ffmpegService = ffmpeg
        _fileManager = fileManager
        _model = model
    End Sub

    Public Async Function ExportAsync(
        options As ExportOptions,
        progress As IProgress(Of ExportProgressData),
        infoCallback As Action(Of String),
        warningCallback As Action(Of String),
        askOverwriteCallback As Func(Of String, Boolean),
        cancellationToken As CancellationToken) As Task(Of Boolean)

        Log.Information("=== ЗАПУСК ПРОЦЕССА ЭКСПОРТА ===")
        Log.Information($"[Входные данные] Файл: {options.SourceFile}")
        Log.Information($"[Формат] Контейнер: {options.SelectedFormat}, Энкодер: {options.VideoEncoder}, Качество: {options.CompressionLevel}")

        Dim isBluray As Boolean = options.SourceFile.StartsWith("bluray:", StringComparison.OrdinalIgnoreCase)
        Dim cleanSourceFile As String = If(isBluray, options.SourceFile.Substring(7), options.SourceFile)

        If Not isBluray AndAlso Not IO.File.Exists(cleanSourceFile) Then
            Log.Error($"[Ошибка] Исходный файл не найден: {cleanSourceFile}")
            warningCallback("Исходный файл не найден." & vbCrLf & "Возможно, он был перемещен или удален.")
            infoCallback("Ошибка: Файл отсутствует.")
            Return False
        End If

        Dim hasCuts As Boolean = _model.Cuts.Count > 0
        Dim targetDurationSec As Double = CalculateTargetDuration(hasCuts)
        If targetDurationSec <= 0 Then targetDurationSec = _model.TotalDuration.TotalSeconds

        Dim isOutputImage As Boolean = options.SelectedFormat.StartsWith("Image", StringComparison.OrdinalIgnoreCase)

        If targetDurationSec <= 0 AndAlso Not options.InputHasImage AndAlso Not isOutputImage Then
            Log.Warning("[Отмена] Зоны вырезания полностью перекрывают длительность медиа.")
            infoCallback("Невозможно сохранить файл: выбранные зоны вырезания полностью перекрывают всю длительность медиа.")
            Return False
        End If

        If Not isOutputImage AndAlso Not options.InputHasImage AndAlso targetDurationSec < 0.1 Then
            Log.Warning($"[Отмена] Результирующее видео слишком короткое: {targetDurationSec} сек.")
            warningCallback("Результирующее видео слишком короткое (менее 0.1 сек)." & vbCrLf & "Измените маркеры или зоны вырезания.")
            infoCallback("Ошибка: Длительность видео слишком мала.")
            Return False
        End If

        Dim ext As String = FFmpegCommandBuilder.GetOutputExtension(options.SelectedFormat)
        Dim warningMsg As String = String.Empty
        Dim outputDirectory As String = _fileManager.GetDownloadsDirectory(warningMsg)

        Dim baseName As String = Path.GetFileNameWithoutExtension(cleanSourceFile)
        Dim outputFile As String = Path.Combine(outputDirectory, baseName & "_processed." & ext)

        Log.Information($"[Выходные данные] Целевой файл: {outputFile}")

        If IO.File.Exists(outputFile) Then
            Log.Information("[Ожидание] Файл уже существует. Ожидание решения пользователя о перезаписи...")
            If Not askOverwriteCallback(outputFile) Then
                Log.Information("[Отмена] Пользователь отказался от перезаписи файла.")
                infoCallback("Экспорт отменен пользователем (файл не перезаписан).")
                Return False
            End If
            Try
                IO.File.Delete(outputFile)
                Log.Information("[Успех] Существующий файл успешно удален.")
            Catch ex As Exception
                Log.Error($"[Ошибка] Не удалось удалить файл для перезаписи: {ex.Message}")
                warningCallback($"Не удалось удалить существующий файл для перезаписи: {ex.Message}")
                infoCallback("Ошибка перезаписи файла.")
                Return False
            End Try
        End If

        If options.TargetWidth > 0 OrElse options.TargetHeight > 0 Then
            Log.Information($"[Трансформация] Масштабирование до: {options.TargetWidth}x{options.TargetHeight}")
        End If
        If options.CropW > 0 AndAlso options.CropH > 0 Then
            Log.Information($"[Трансформация] Кадрирование: {options.CropW}x{options.CropH} (Смещение X:{options.CropX}, Y:{options.CropY})")
        End If
        If options.VideoFadeIn > TimeSpan.Zero OrElse options.VideoFadeOut > TimeSpan.Zero Then
            Log.Information($"[Эффекты] Видео Fade-In: {options.VideoFadeIn.TotalSeconds}с, Fade-Out: {options.VideoFadeOut.TotalSeconds}с")
        End If

        If options.IsAudioReplaced Then
            Log.Information($"[Аудио] Заменено на внешний файл: {options.ExternalAudioPath}")
        End If
        If options.AudioOffset <> TimeSpan.Zero Then
            Log.Information($"[Аудио] Применено смещение: {options.AudioOffset.TotalMilliseconds} мс")
        End If
        If options.TrackVolume <> 1.0F Then
            Log.Information($"[Аудио] Громкость: {CInt(options.TrackVolume * 100)}%")
        End If
        If options.AudioFadeIn > TimeSpan.Zero OrElse options.AudioFadeOut > TimeSpan.Zero Then
            Log.Information($"[Эффекты] Аудио Fade-In: {options.AudioFadeIn.TotalSeconds}с, Fade-Out: {options.AudioFadeOut.TotalSeconds}с")
        End If

        Log.Information($"[Ускорение] NVENC: {options.IsNvidiaGpuSelected}, AMF: {options.IsAmdGpuSelected}")
        Log.Information($"[Хронометраж] Зон для вырезания: {_model.Cuts.Count}, Маркеры: {options.ExtractTime} -> {options.ExtractTime.Add(TimeSpan.FromSeconds(targetDurationSec))}")

        Dim isStaticImageFormat As Boolean = {"jpg", "jpeg", "png", "bmp", "avif", "jxl"}.Contains(ext)
        If isOutputImage AndAlso Not options.InputHasImage AndAlso isStaticImageFormat Then
            Log.Information($"[Режим] Экспорт статического кадра на позиции {options.ExtractTime}")
            infoCallback("Извлечение текущего кадра в высоком качестве...")
            progress?.Report(New ExportProgressData(50, ""))

            Dim success As Boolean = Await SaveFrameAtTimeAsync(options.SourceFile, options.ExtractTime, outputFile, ext, options.TargetWidth, options.TargetHeight, options.CropW, options.CropH, options.CropX, options.CropY, cancellationToken)
            If success Then
                Log.Information("=== КАДР УСПЕШНО СОХРАНЕН ===")
                progress?.Report(New ExportProgressData(100, "00:00:00"))
                infoCallback("Кадр успешно сохранен")
                Return True
            Else
                Log.Error("=== ОШИБКА СОХРАНЕНИЯ КАДРА ===")
                progress?.Report(New ExportProgressData(0, ""))
                infoCallback("Ошибка при сохранении кадра")
                Return False
            End If
        End If

        infoCallback("Анализ и подготовка параметров экспорта...")
        Dim mediaInfo As FFmpegService.MediaInfo = Await _ffmpegService.GetMediaInfoAsync(options.SourceFile)
        Dim threadsCount As Integer = Environment.ProcessorCount

        Dim ffmpegCuts As New List(Of FFmpegCutRegion)()
        If hasCuts Then
            For Each cut In _model.Cuts
                ffmpegCuts.Add(New FFmpegCutRegion(cut.StartTime, cut.EndTime))
            Next
        End If

        Dim buildResult = FFmpegCommandBuilder.BuildExportCommandWithCuts(
            options.SourceFile, ffmpegCuts.AsReadOnly(), mediaInfo.HasAudio,
            options.SelectedFormat, options.VideoEncoder, options.CompressionLevel,
            outputFile, threadsCount,
            options.IsNvidiaGpuSelected, options.IsAmdGpuSelected,
            _model.MarkerStart, _model.MarkerEnd,
            options.TargetWidth, options.TargetHeight, options.InputHasImage,
            options.CropW, options.CropH, options.CropX, options.CropY,
            options.IsAudioReplaced, options.ExternalAudioPath, options.AudioOffset,
            options.AudioFadeIn, options.AudioFadeOut, options.TrackVolume,
            options.VideoFadeIn, options.VideoFadeOut, options.SourceFps)

        Dim arguments As String = buildResult.Arguments

        If String.IsNullOrEmpty(arguments) Then
            Log.Error("[Ошибка] Строка аргументов FFmpeg пуста. Кэширование или фильтры вернули некорректный ответ.")
            infoCallback("Произошла критическая ошибка при попытке сформировать аргументы FFmpeg.")
            Return False
        End If

        Log.Information($"[Выполнение FFmpeg] Аргументы: {arguments}")

        Dim requestedCopy As Boolean = Not String.IsNullOrEmpty(options.VideoEncoder) AndAlso options.VideoEncoder.Contains("Copy", StringComparison.OrdinalIgnoreCase)
        If buildResult.IsFallbackApplied OrElse (requestedCopy AndAlso buildResult.ActualEncoder <> "copy") Then
            Log.Warning($"[Fallback] Аппаратное/прямое копирование отключено из-за сложных фильтров. Использован кодек: {buildResult.ActualEncoder}")
            warningCallback("Внимание: Выбранный режим копирования 'Copy' несовместим с текущими эффектами (вырезание, кадрирование, фейды или изменение разрешения). Видео будет перекодировано с использованием совместимого кодека.")
        End If

        progress?.Report(New ExportProgressData(0, ""))
        infoCallback("Конвертация медиафайла...")

        Dim progressIndicator As New Progress(Of FFmpegService.FFmpegProgress)(
            Sub(p)
                Dim timeStr As String = String.Format("{0:hh\:mm\:ss}", p.TimeRemaining)
                progress?.Report(New ExportProgressData(p.ProgressPercentage, timeStr))
            End Sub)

        Dim exitCode As Integer = Await _ffmpegService.StartFFmpegWithProgressAsync(arguments, targetDurationSec, progressIndicator, cancellationToken)

        If cancellationToken.IsCancellationRequested Then
            Log.Information("=== КОНВЕРТАЦИЯ ОТМЕНЕНА ПОЛЬЗОВАТЕЛЕМ ===")
            infoCallback("Процесс конвертации отменен пользователем.")
            progress?.Report(New ExportProgressData(0, ""))
            Return False
        ElseIf exitCode = 0 Then
            Log.Information($"=== КОНВЕРТАЦИЯ УСПЕШНО ЗАВЕРШЕНА ===")
            progress?.Report(New ExportProgressData(100, "00:00:00"))
            infoCallback("Конвертация файла прошла успешно")
            Return True
        Else
            Log.Error($"=== ОШИБКА КОНВЕРТАЦИИ (Код выхода FFmpeg: {exitCode}) ===")
            infoCallback($"Процесс FFmpeg завершился с ошибкой." & vbCrLf & $"Код: {exitCode}")
            Return False
        End If
    End Function

    Private Function CalculateTargetDuration(hasCuts As Boolean) As Double
        Dim targetDurationSec As Double = 0
        If hasCuts Then
            Dim currentStart As TimeSpan = _model.MarkerStart
            For Each cut In _model.Cuts
                If cut.EndTime <= _model.MarkerStart Then Continue For
                If cut.StartTime >= _model.MarkerEnd Then Continue For
                Dim effCutStart As TimeSpan = If(cut.StartTime < _model.MarkerStart, _model.MarkerStart, cut.StartTime)
                Dim effCutEnd As TimeSpan = If(cut.EndTime > _model.MarkerEnd, _model.MarkerEnd, cut.EndTime)
                If effCutStart > currentStart Then targetDurationSec += (effCutStart - currentStart).TotalSeconds
                If effCutEnd > currentStart Then currentStart = effCutEnd
            Next
            If currentStart < _model.MarkerEnd Then targetDurationSec += (_model.MarkerEnd - currentStart).TotalSeconds
        Else
            targetDurationSec = (_model.MarkerEnd - _model.MarkerStart).TotalSeconds
        End If
        Return targetDurationSec
    End Function

    Private Async Function SaveFrameAtTimeAsync(videoPath As String, position As TimeSpan, outputPath As String, outExt As String, targetW As Integer, targetH As Integer, cropW As Integer, cropH As Integer, cropX As Integer, cropY As Integer, token As CancellationToken) As Task(Of Boolean)
        If Not _ffmpegService.CheckFFmpeg() Then Return False

        Dim timeStr As String = FFmpegCommandBuilder.FormatTimeForFFmpeg(position)
        Dim ext As String = outExt.ToLowerInvariant()
        Dim args As String
        Dim videoFilters As New List(Of String)()

        If cropW > 0 AndAlso cropH > 0 Then
            videoFilters.Add($"crop={cropW}:{cropH}:{cropX}:{cropY}")
        End If
        If targetW > 0 AndAlso targetH > 0 Then
            videoFilters.Add($"scale={targetW}:{targetH}:force_original_aspect_ratio=decrease,pad={targetW}:{targetH}:(ow-iw)/2:(oh-ih)/2:black")
        End If

        Dim vf As String = If(videoFilters.Count > 0, $" -vf ""{String.Join(",", videoFilters)}""", "")

        Select Case ext
            Case "avif"
                args = $"-hide_banner -loglevel error -ss {timeStr} -i ""{videoPath}"" -vframes 1{vf} -c:v libsvtav1 -preset 4 -crf 15 -y ""{outputPath}"""
            Case "webp"
                args = $"-hide_banner -loglevel error -ss {timeStr} -i ""{videoPath}"" -vframes 1{vf} -c:v libwebp -lossless 1 -y ""{outputPath}"""
            Case "png"
                args = $"-hide_banner -loglevel error -ss {timeStr} -i ""{videoPath}"" -vframes 1{vf} -c:v png -compression_level 1 -y ""{outputPath}"""
            Case "jpg", "jpeg"
                args = $"-hide_banner -loglevel error -ss {timeStr} -i ""{videoPath}"" -vframes 1{vf} -c:v mjpeg -q:v 2 -y ""{outputPath}"""
            Case "gif"
                args = $"-hide_banner -loglevel error -ss {timeStr} -i ""{videoPath}"" -vframes 1{vf} -c:v gif -y ""{outputPath}"""
            Case "jxl"
                args = $"-hide_banner -loglevel error -ss {timeStr} -i ""{videoPath}"" -vframes 1{vf} -c:v libjxl -q:v 100 -strict experimental -y ""{outputPath}"""
            Case "bmp"
                args = $"-hide_banner -loglevel error -ss {timeStr} -i ""{videoPath}"" -vframes 1{vf} -c:v bmp -y ""{outputPath}"""
            Case Else
                args = $"-hide_banner -loglevel error -ss {timeStr} -i ""{videoPath}"" -vframes 1{vf} -q:v 2 -y ""{outputPath}"""
        End Select

        Log.Information($"[FFmpeg Frame] {args}")

        Try
            Using linkedCts As CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token)
                linkedCts.CancelAfter(15000)
                Dim result As FFmpegService.ProcessResult = Await _ffmpegService.RunProcessCaptureAsync(_ffmpegService.GetFFmpegPath(), args, 15000, linkedCts.Token)
                Return result.ExitCode = 0 AndAlso IO.File.Exists(outputPath)
            End Using
        Catch ex As Exception
            Log.Error($"[Ошибка] Извлечение кадра прервано: {ex.Message}")
            Return False
        End Try
    End Function
End Class

Public Class MainEditorPresenter
    Private ReadOnly _view As IMainEditorView
    Private ReadOnly _model As ProjectModel
    Private ReadOnly _renderer As ITimelineRenderer
    Private ReadOnly _player As IMediaPlayerManager
    Private ReadOnly _ffmpegService As FFmpegService
    Private ReadOnly _fileManager As FileManager
    Private ReadOnly _exportService As ExportService

    Private _lastSkipTime As DateTime = DateTime.MinValue
    Private ReadOnly _conversionCts As CancellationTokenSource
    Private ReadOnly _exportLock As New Object()

    Private ReadOnly _previewCache As New PreviewCacheManager(30)

    Private _currentMediaInfo As FFmpegService.MediaInfo
    Private _previewGenerationRevision As Long = 0
    Private _previewCts As CancellationTokenSource

    Public Sub New(view As IMainEditorView, model As ProjectModel, renderer As ITimelineRenderer, player As IMediaPlayerManager, ffmpeg As FFmpegService, fileManager As FileManager)
        _view = view
        _model = model
        _renderer = renderer
        _player = player
        _ffmpegService = ffmpeg
        _fileManager = fileManager

        _exportService = New ExportService(_ffmpegService, _fileManager, _model)

        AddHandler _view.CutRequested, AddressOf OnCutRequested
        AddHandler _view.ClearCutsRequested, AddressOf OnClearCutsRequested
        AddHandler _view.ZoomInRequested, AddressOf OnZoomInRequested
        AddHandler _view.ZoomOutRequested, AddressOf OnZoomOutRequested
        AddHandler _view.PlaybackTick, AddressOf OnPlaybackTick

        AddHandler _model.StateChanged, AddressOf SyncViewWithModel
    End Sub

    Public Async Function ProcessMediaFilesAsync(rawText As String) As Task
        _player.StopPlayback()
        _view.ShowLoadingState("Анализ файлов...")

        Dim files As List(Of String) = FileManager.ParseInputFiles(rawText)
        _view.SetPreviewImage(Nothing)

        If files IsNot Nothing AndAlso files.Count > 0 Then
            Dim currentFile As String = files(0)

            Try
                _currentMediaInfo = Await _ffmpegService.GetMediaInfoAsync(currentFile)

                Dim isAudioOnly As Boolean = (_currentMediaInfo.Width = 0 AndAlso _currentMediaInfo.Height = 0 AndAlso _currentMediaInfo.HasAudio)

                _model.ClearAllClips()

                Dim clip As New MediaClip() With {
                    .FilePath = currentFile,
                    .SourceDuration = _currentMediaInfo.Duration,
                    .SourceIn = TimeSpan.Zero,
                    .SourceOut = _currentMediaInfo.Duration,
                    .TimelineStart = TimeSpan.Zero,
                    .MediaType = If(isAudioOnly, TargetFormatType.Audio, TargetFormatType.Video)
                }

                Dim trackIndex As Integer = If(isAudioOnly, 1, 0)
                _model.AddClipToTrack(trackIndex, clip)

                _model.SetMarkers(TimeSpan.Zero, _model.TotalDuration)
                _model.ClearCuts()
                _model.ResetZoomHistory()

                Dim infoText As String = BuildMediaInfoText(_currentMediaInfo)
                _view.UpdateMediaInfoUI(infoText, True)
                _view.UpdateResolutionProfiles(_currentMediaInfo.Width, _currentMediaInfo.Height)
                _view.SetHardwareControlsState(isAudioOnly)

                Await BuildTimelineAsync()
                Await RequestPreviewAsync(currentFile, TimeSpan.Zero)

            Catch ex As Exception
                _view.ShowWarningMessage($"Ошибка при анализе файла: {ex.Message}")
            Finally
                _view.HideLoadingState()
            End Try
        Else
            _model.ClearAllClips()
            _view.UpdateMediaInfoUI("Файл не выбран", False)
            _renderer.SetDataSources(Nothing, Nothing)
            _renderer.ClearStrips()

            _previewCache.Clear()
            _view.HideLoadingState()
        End If
    End Function

    Private Shared Function BuildTimelineAsync() As Task
        Return Task.CompletedTask
    End Function

    Public Async Function RequestPreviewAsync(videoPath As String, targetTime As TimeSpan) As Task
        If _currentMediaInfo.Width = 0 AndAlso _currentMediaInfo.Height = 0 Then Return

        Dim currentRevision As Long = Interlocked.Increment(_previewGenerationRevision)

        Dim newCts As New CancellationTokenSource()
        Dim oldCts As CancellationTokenSource = Interlocked.Exchange(_previewCts, newCts)
        If oldCts IsNot Nothing Then
            Try
                oldCts.Cancel()
                oldCts.Dispose()
            Catch
            End Try
        End If

        Dim baseW As Integer = 1920
        Dim targetW As Integer = baseW
        Dim targetH As Integer = 1080

        If _currentMediaInfo.Width > 0 AndAlso _currentMediaInfo.Height > 0 Then
            Dim sourceAspect As Double = _currentMediaInfo.Width / _currentMediaInfo.Height
            targetH = CInt(baseW / sourceAspect)
            targetW = (targetW \ 2) * 2
            targetH = (targetH \ 2) * 2
            If targetW = 0 Then targetW = 2
            If targetH = 0 Then targetH = 2
        End If

        Dim cacheKey As String = $"{videoPath}_{targetTime.TotalMilliseconds}_{targetW}x{targetH}"
        Dim handle As PreviewCacheManager.CachedFrameHandle = _previewCache.TryGet(cacheKey, targetW, targetH)

        If handle Is Nothing Then
            Dim newFrame As PooledFrameBuffer = Await _ffmpegService.ExtractPreviewFrameFromPipeAsync(videoPath, targetTime, targetW, targetH, newCts.Token)
            If Not newCts.Token.IsCancellationRequested AndAlso newFrame IsNot Nothing AndAlso newFrame.Size > 0 Then
                _previewCache.Add(cacheKey, newFrame, targetW, targetH)
                handle = _previewCache.TryGet(cacheKey, targetW, targetH)
            Else
                If newFrame IsNot Nothing Then newFrame.Dispose()
            End If
        End If

        If handle IsNot Nothing Then
            Using handle
                Dim cachedFrame As PooledFrameBuffer = handle.Buffer
                If currentRevision = Interlocked.Read(_previewGenerationRevision) Then
                    Dim previewBmp As Bitmap = Nothing
                    Try
                        previewBmp = New Bitmap(targetW, targetH, Imaging.PixelFormat.Format32bppArgb)
                        Dim bmpData As Imaging.BitmapData = previewBmp.LockBits(New Rectangle(0, 0, targetW, targetH), Imaging.ImageLockMode.WriteOnly, previewBmp.PixelFormat)
                        Marshal.Copy(cachedFrame.Buffer, 0, bmpData.Scan0, cachedFrame.Size)
                        previewBmp.UnlockBits(bmpData)

                        _view.SetPreviewImage(previewBmp)
                    Catch ex As Exception
                        If previewBmp IsNot Nothing Then previewBmp.Dispose()
                    End Try
                End If
            End Using
        End If
    End Function

    Private Shared Function BuildMediaInfoText(info As FFmpegService.MediaInfo) As String
        If info.Width = 0 AndAlso info.Height = 0 Then
            Return $"Формат: {info.Codec} | {info.Bitrate} kbps"
        End If
        Dim fpsStr As String = If(info.Fps > 0, info.Fps.ToString("F2", CultureInfo.InvariantCulture), "N/A")
        Return $"{info.Width}x{info.Height} | {info.Codec} | {fpsStr} fps | {info.Bitrate} kbps"
    End Function

    Private Sub OnPlaybackTick(sender As Object, currentVlcMs As Long)
        Dim physicalTime As TimeSpan = TimeSpan.FromMilliseconds(currentVlcMs)

        Dim skipTarget = _model.GetSkipTargetIfInCut(physicalTime)
        If skipTarget.HasValue Then
            If (DateTime.Now - _lastSkipTime).TotalMilliseconds > 500 Then
                _lastSkipTime = DateTime.Now
                Dim safeTarget As TimeSpan = skipTarget.Value.Add(TimeSpan.FromMilliseconds(10))
                _view.RequestPlayerSeek(safeTarget)
            End If

            Dim freezeVirtualTime As TimeSpan = _model.PhysicalToVirtualTime(physicalTime)
            _view.UpdatePlayheadUI(freezeVirtualTime)
            Return
        End If

        Dim virtualTime As TimeSpan = _model.PhysicalToVirtualTime(physicalTime)
        Dim stopVirtualTime As TimeSpan = _model.PhysicalToVirtualTime(If(_model.IsZoomed, _model.ViewEnd, _model.MarkerEnd))

        If virtualTime >= stopVirtualTime Then
            _view.UpdatePlayheadUI(stopVirtualTime)
            _view.StopPlayerUI()
            _view.ShowInfoMessage("Достигнут маркер конца. Воспроизведение остановлено.")
            Return
        End If

        _view.UpdatePlayheadUI(virtualTime)
    End Sub

    Private Sub OnCutRequested(sender As Object, e As EventArgs)
        If _view.MarkerStart >= _view.MarkerEnd Then
            _view.ShowWarningMessage("Конечный маркер должен стоять строго после начального.")
            Return
        End If
        _model.AddCutRegion(_view.MarkerStart, _view.MarkerEnd)
        _view.ShowInfoMessage("Участок успешно вырезан")
    End Sub

    Private Sub OnClearCutsRequested(sender As Object, e As EventArgs)
        If _model.Cuts.Count = 0 Then
            _view.ShowInfoMessage("Нет вырезанных участков для отмены.")
            Return
        End If
        _model.ClearCuts()
        _view.ShowInfoMessage("Вырезание отменено")
    End Sub

    Private Sub OnZoomInRequested(sender As Object, e As EventArgs)
        If (_view.MarkerEnd - _view.MarkerStart).TotalSeconds < 0.1 Then Return
        _model.ZoomIn(_view.MarkerStart, _view.MarkerEnd)
        SyncViewWithModel()
    End Sub

    Private Sub OnZoomOutRequested(sender As Object, e As EventArgs)
        _model.ZoomOut()
        SyncViewWithModel()
    End Sub

    Private Sub SyncViewWithModel()
        Dim stateData = _model.GetTimelineStateData()
        _view.RenderTimelineState(stateData, 30.0, True, False, True)
    End Sub

    Public Sub CancelExport()
        SyncLock _exportLock
            If _conversionCts IsNot Nothing AndAlso Not _conversionCts.IsCancellationRequested Then
                Log.Warning("[Отмена] Вызов CancelExport: Пользователь прервал процесс вручную.")
                _conversionCts.Cancel()
            End If
        End SyncLock
    End Sub

    Public Async Function ExportMediaAsync(options As ExportOptions) As Task
        _view.SetExportState(True)
        _view.ShowInfoMessage("Подготовка к экспорту проекта...")

        Try
            ' 1. Формируем путь для сохранения файла
            Dim saveDialog As New SaveFileDialog() With {
            .Filter = "MP4 Video (*.mp4)|*.mp4|All files (*.*)|*.*",
            .Title = "Экспорт проекта",
            .FileName = "MyNleProject.mp4"
        }

            If saveDialog.ShowDialog() <> DialogResult.OK Then
                _view.SetExportState(False)
                _view.ShowInfoMessage("Экспорт отменен.")
                Return
            End If

            Dim outputFilePath As String = saveDialog.FileName

            ' 2. Определяем системное имя кодека для FFmpeg
            Dim ffmpegEncoder As String = "libx264"
            If options.IsNvidiaGpuSelected Then
                ffmpegEncoder = "h264_nvenc"
            ElseIf options.IsAmdGpuSelected Then
                ffmpegEncoder = "h264_amf"
            End If

            ' Защита от нулевого разрешения
            Dim targetW As Integer = If(options.TargetWidth > 0, options.TargetWidth, 1920)
            Dim targetH As Integer = If(options.TargetHeight > 0, options.TargetHeight, 1080)
            Dim targetFps As Double = Math.Max(24.0, options.SourceFps)

            ' 3. ГЕНЕРИРУЕМ NLE-ГРАФ ФИЛЬТРОВ
            Dim ffmpegArgs As String = NleExportGraphBuilder.BuildExportArguments(
            _model,
            outputFilePath,
            targetW,
            targetH,
            targetFps,
            ffmpegEncoder,
            options.CompressionLevel, ' Передаем выбранный уровень из UI
            options.IsNvidiaGpuSelected,
            options.IsAmdGpuSelected
        )

            If String.IsNullOrWhiteSpace(ffmpegArgs) Then
                _view.ShowWarningMessage("Таймлайн пуст или не содержит валидных клипов для экспорта.")
                Return
            End If

            ' 4. Настраиваем прогресс-бар
            Dim totalDurationSec As Double = (_model.MarkerEnd - _model.MarkerStart).TotalSeconds
            Dim progress = New Progress(Of FFmpegService.FFmpegProgress)(Sub(p)
                                                                             Dim timeRemainingStr As String = If(p.TimeRemaining.TotalSeconds > 0, p.TimeRemaining.ToString("hh\:mm\:ss"), "Вычисление...")
                                                                             _view.UpdateExportProgress(p.ProgressPercentage, timeRemainingStr)
                                                                         End Sub)

            ' 5. Запускаем рендер
            _view.ShowInfoMessage("Идет рендеринг проекта...")

            Dim exitCode As Integer = Await _ffmpegService.StartFFmpegWithProgressAsync(
            ffmpegArgs,
            totalDurationSec,
            progress,
            CancellationToken.None
        )

            If exitCode = 0 AndAlso System.IO.File.Exists(outputFilePath) Then
                _view.ShowInfoMessage("Экспорт успешно завершен!")
            Else
                _view.ShowWarningMessage($"Произошла ошибка при экспорте. Код: {exitCode}")
            End If

        Catch ex As Exception
            _view.ShowWarningMessage($"Критическая ошибка экспорта: {ex.Message}")
        Finally
            _view.SetExportState(False)
        End Try
    End Function

    Public Sub Shutdown()
        CancelExport()

        If _previewCts IsNot Nothing Then
            Try
                _previewCts.Cancel()
                _previewCts.Dispose()
            Catch
            End Try
        End If

        _previewCache.Clear()
    End Sub
End Class