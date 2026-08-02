Option Strict On
Option Explicit On

Imports System.Buffers
Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Globalization
Imports System.Diagnostics
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Runtime.InteropServices
Imports System.Collections.Concurrent
Imports yoump.IServices
Imports yoump.Native

Public Class FFmpegService
    Implements IFFmpegService
    Implements IDisposable, IAsyncDisposable

    Public Structure MediaInfo
        Public Duration As TimeSpan
        Public Bitrate As String
        Public Codec As String
        Public Fps As Double
        Public Width As Integer
        Public Height As Integer
        Public HasAudio As Boolean
    End Structure

    Public Structure ProcessResult
        Public ExitCode As Integer
        Public StdOut As String
        Public StdErr As String
    End Structure

    Public Structure FFmpegProgress
        Public Property ProgressPercentage As Integer
        Public Property TimeRemaining As TimeSpan
        Public Property AverageSpeed As Double
        Public Property Message As String
    End Structure

    Private Class RefCountedLock
        Public ReadOnly Semaphore As New SemaphoreSlim(1, 1)
        Public RefCount As Integer = 0
    End Class

    Private ReadOnly _locksSyncObj As New Object()
    Private ReadOnly _mediaInfoLocks As New Dictionary(Of String, RefCountedLock)()

    Private Shared ReadOnly StrictStaticExtensions As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        ".jpg", ".jpeg", ".png", ".bmp", ".avif", ".jxl"
    }

    Private Shared ReadOnly PotentialAnimatedExtensions As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        ".gif", ".webp"
    }

    Private ReadOnly mediaInfoCache As New ConcurrentDictionary(Of String, MediaInfo)()
    Private ReadOnly _processTaskRunner As New SemaphoreSlim(5, 5)
    Private Shared ReadOnly SpeedRegex As New Regex("speed=\s*([\d.]+)x", RegexOptions.Compiled)
    Private Shared ReadOnly TimeRegex As New Regex("time=\s*(-?)(\d+):(\d{2}):(\d{2})(?:\.(\d+))?", RegexOptions.Compiled)

    Public Event LogMessage As Action(Of String) Implements IFFmpegService.LogMessage

    Private ReadOnly _baseAppPath As String
    Private _activeFFmpegProcess As Process = Nothing
    Private ReadOnly activeProcessLock As New Object()
    Private Shared ReadOnly JsonOptions As New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True}

    Private ReadOnly _jobHandle As IntPtr = IntPtr.Zero

    Private ReadOnly _globalCts As New CancellationTokenSource()
    Private ReadOnly _activeProcesses As New ConcurrentDictionary(Of Process, Byte)()

    Public Property ActiveFFmpegProcess As Process Implements IFFmpegService.ActiveFFmpegProcess
        Get
            SyncLock activeProcessLock
                Return _activeFFmpegProcess
            End SyncLock
        End Get
        Set(value As Process)
            SyncLock activeProcessLock
                _activeFFmpegProcess = value
            End SyncLock
        End Set
    End Property

    Public Sub New(baseAppPath As String)
        _baseAppPath = baseAppPath

        Try
            _jobHandle = CreateJobObject(IntPtr.Zero, Nothing)
            If _jobHandle <> IntPtr.Zero Then
                Dim info As New JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE

                Dim length As Integer = Marshal.SizeOf(Of JOBOBJECT_EXTENDED_LIMIT_INFORMATION)()
                Dim extendedInfoPtr As IntPtr = Marshal.AllocHGlobal(length)

                Try
                    Marshal.StructureToPtr(info, extendedInfoPtr, False)
                    SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, extendedInfoPtr, length)
                Finally
                    Marshal.FreeHGlobal(extendedInfoPtr)
                End Try
            End If
        Catch ex As Exception
            ' Игнорируем ошибки доступа
        End Try
    End Sub

    Private Sub QueueLog(msg As String)
        RaiseEvent LogMessage(msg)
    End Sub

    Private Sub RegisterProcess(proc As Process)
        If proc IsNot Nothing Then
            _activeProcesses.TryAdd(proc, 0)
        End If
    End Sub

    Private Sub UnregisterProcess(proc As Process)
        If proc IsNot Nothing Then
            _activeProcesses.TryRemove(proc, Nothing)
        End If
    End Sub

    Public Function GetFFmpegPath() As String Implements IFFmpegService.GetFFmpegPath
        Dim localPath As String = Path.Combine(_baseAppPath, "ffmpeg\bin\ffmpeg.exe")
        If File.Exists(localPath) Then
            Return localPath
        Else
            Return "ffmpeg.exe"
        End If
    End Function

    Public Function GetFFprobePath() As String Implements IFFmpegService.GetFFprobePath
        Dim localPath As String = Path.Combine(_baseAppPath, "ffmpeg\bin\ffprobe.exe")
        If File.Exists(localPath) Then
            Return localPath
        Else
            Return "ffprobe.exe"
        End If
    End Function

    Public Function CheckFFmpeg() As Boolean Implements IFFmpegService.CheckFFmpeg
        Dim localPath As String = GetFFmpegPath()
        If File.Exists(localPath) Then Return True

        Try
            Dim psi As New ProcessStartInfo("ffmpeg", "-version") With {
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True
            }
            Using proc As Process = Process.Start(psi)
                If proc IsNot Nothing Then
                    Dim exited As Boolean = proc.WaitForExit(2000)
                    If Not exited Then
                        Try
                            proc.Kill()
                        Catch
                        End Try
                    End If
                    Return exited AndAlso proc.ExitCode = 0
                End If
            End Using
        Catch
        End Try

        Return False
    End Function

    Public Async Function ExtractPreviewFrameFromPipeAsync(videoFilePath As String, timePosition As TimeSpan, targetWidth As Integer, targetHeight As Integer, token As CancellationToken) As Task(Of PooledFrameBuffer) Implements IFFmpegService.ExtractPreviewFrameFromPipeAsync
        Dim expectedSize As Integer = targetWidth * targetHeight * 4
        Dim buffer As Byte() = ArrayPool(Of Byte).Shared.Rent(expectedSize)
        Dim success As Boolean = False

        Try
            Using linkedCts As CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, _globalCts.Token)
                Await Task.Run(Sub()
                                   Try
                                       Using decoder As New NativeMediaDecoder(videoFilePath)
                                           Dim handle As GCHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned)
                                           Try
                                               success = decoder.ExtractFrameToBuffer(timePosition, handle.AddrOfPinnedObject(), targetWidth, targetHeight, targetWidth * 4)
                                           Finally
                                               handle.Free()
                                           End Try
                                       End Using
                                   Catch ex As Exception
                                       QueueLog($"[FFmpegService] Ошибка нативного превью: {ex.Message}")
                                   End Try
                               End Sub, linkedCts.Token).ConfigureAwait(False)
            End Using

            If success Then
                Return New PooledFrameBuffer(buffer, expectedSize)
            End If
            Return Nothing
        Finally
            If Not success Then
                ArrayPool(Of Byte).Shared.Return(buffer)
            End If
        End Try
    End Function

    Public Async Function ExtractFramePooledAsync(videoFilePath As String, timePosition As TimeSpan, width As Integer, height As Integer, token As CancellationToken) As Task(Of PooledFrameBuffer) Implements IFFmpegService.ExtractFramePooledAsync
        Return Await ExtractPreviewFrameFromPipeAsync(videoFilePath, timePosition, width, height, token).ConfigureAwait(False)
    End Function



    Private Shared Function ParseAnyDuration(val As String) As TimeSpan
        If String.IsNullOrWhiteSpace(val) OrElse val = "N/A" Then Return TimeSpan.Zero

        Dim cleanVal As String = val.Trim()

        If cleanVal.Contains("s"c) OrElse cleanVal.Contains("m"c) OrElse cleanVal.Contains("h"c) Then
            Try
                Dim totalMs As Double = 0.0
                Dim matches As MatchCollection = Regex.Matches(cleanVal, "(?<val>\d+(?:\.\d+)?)\s*(?<unit>ms|s|m|h)", RegexOptions.IgnoreCase)
                If matches.Count > 0 Then
                    For Each m As Match In matches
                        Dim num As Double
                        If Double.TryParse(m.Groups("val").Value, NumberStyles.Any, CultureInfo.InvariantCulture, num) Then
                            Dim unit As String = m.Groups("unit").Value.ToLowerInvariant()
                            Select Case unit
                                Case "ms" : totalMs += num
                                Case "s" : totalMs += num * 1000.0
                                Case "m" : totalMs += num * 60000.0
                                Case "h" : totalMs += num * 3600000.0
                            End Select
                        End If
                    Next
                    Return TimeSpan.FromMilliseconds(totalMs)
                End If
            Catch
            End Try
        End If

        Dim dSec As Double
        If Double.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, dSec) Then
            Return TimeSpan.FromSeconds(dSec)
        End If

        Dim dotIndex As Integer = cleanVal.LastIndexOf("."c)
        If dotIndex > 0 Then
            Dim fracLength As Integer = cleanVal.Length - dotIndex - 1
            If fracLength > 7 Then
                Dim safeLength As Integer = Math.Min(cleanVal.Length, dotIndex + 8)
                cleanVal = cleanVal.Substring(0, safeLength)
            End If
        End If

        Dim ts As TimeSpan
        If TimeSpan.TryParse(cleanVal, CultureInfo.InvariantCulture, ts) Then
            Return ts
        End If

        Return TimeSpan.Zero
    End Function

    Private Shared Function CreateDefaultMediaInfo() As MediaInfo
        Return New MediaInfo With {
            .Duration = TimeSpan.Zero,
            .Bitrate = "2000",
            .Codec = "N/A",
            .Fps = 0.0,
            .Width = 0,
            .Height = 0,
            .HasAudio = False
        }
    End Function

    Public Async Function BakeAudioShiftAsync(inputPath As String, outputPath As String, offset As TimeSpan, token As CancellationToken) As Task(Of Boolean) Implements IFFmpegService.BakeAudioShiftAsync
        Try
            Dim ffmpegPath As String = GetFFmpegPath()
            Dim args As String
            If offset.TotalMilliseconds > 0 Then
                Dim delayMs As Integer = CInt(offset.TotalMilliseconds)
                args = $"-hide_banner -loglevel error -y -i ""{inputPath}"" -af ""adelay={delayMs}|{delayMs}"" -vn -c:a pcm_s16le -ar 48000 -ac 2 ""{outputPath}"""
            ElseIf offset.TotalMilliseconds < 0 Then
                Dim skipSec As Double = Math.Abs(offset.TotalSeconds)
                Dim skipStr As String = skipSec.ToString(CultureInfo.InvariantCulture)
                args = $"-hide_banner -loglevel error -y -ss {skipStr} -i ""{inputPath}"" -vn -c:a pcm_s16le -ar 48000 -ac 2 ""{outputPath}"""
            Else
                args = $"-hide_banner -loglevel error -y -i ""{inputPath}"" -vn -c:a pcm_s16le -ar 48000 -ac 2 ""{outputPath}"""
            End If

            Dim result As ProcessResult = Await RunProcessCaptureAsync(ffmpegPath, args, 60000, token).ConfigureAwait(False)

            If result.ExitCode <> 0 Then Return False
            Return File.Exists(outputPath)
        Catch exCancel As OperationCanceledException
            If File.Exists(outputPath) Then
                Try
                    File.Delete(outputPath)
                Catch
                End Try
            End If
            Return False
        Catch exBake As Exception
            Return False
        End Try
    End Function

    Public Async Function GetMediaInfoAsync(filePath As String) As Task(Of MediaInfo) Implements IFFmpegService.GetMediaInfoAsync
        Dim cached As MediaInfo = Nothing
        If mediaInfoCache.TryGetValue(filePath, cached) Then Return cached

        Dim refLock As RefCountedLock = Nothing
        Dim isBluray As Boolean = Not String.IsNullOrWhiteSpace(filePath) AndAlso
                                  filePath.StartsWith("bluray:", StringComparison.OrdinalIgnoreCase)

        If String.IsNullOrWhiteSpace(filePath) OrElse (Not isBluray AndAlso Not File.Exists(filePath)) Then
            Return CreateDefaultMediaInfo()
        End If

        Try
            If Not isBluray Then
                Dim fileInf As New FileInfo(filePath)
                If fileInf.Length = 0 Then Return CreateDefaultMediaInfo()
            End If
        Catch exFile As Exception
            Return CreateDefaultMediaInfo()
        End Try

        SyncLock _locksSyncObj
            If Not _mediaInfoLocks.TryGetValue(filePath, refLock) Then
                refLock = New RefCountedLock()
                _mediaInfoLocks.Add(filePath, refLock)
            End If
            refLock.RefCount += 1
        End SyncLock

        Dim lockAcquired As Boolean = False
        Try
            Try
                lockAcquired = Await refLock.Semaphore.WaitAsync(15000, _globalCts.Token).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                Return CreateDefaultMediaInfo()
            End Try

            If Not lockAcquired Then Return CreateDefaultMediaInfo()

            If mediaInfoCache.TryGetValue(filePath, cached) Then Return cached

            Dim rawDuration As TimeSpan = TimeSpan.Zero
            Dim bitrate As String = "2500"
            Dim codec As String = "N/A"
            Dim fps As Double = 0.0
            Dim w As Integer = 0
            Dim h As Integer = 0
            Dim hasAudio As Boolean = False
            Dim hasVideo As Boolean = False

            Dim ext As String = If(isBluray, ".bdmv", Path.GetExtension(filePath).ToLowerInvariant())
            Dim isStrictStaticImage As Boolean = StrictStaticExtensions.Contains(ext)
            Dim isPotentialAnimatedImage As Boolean = PotentialAnimatedExtensions.Contains(ext)

            Try
                Using cts As New CancellationTokenSource(10000)
                    Dim args As String = $"-v quiet -print_format json -show_format -show_streams ""{filePath}"""
                    Dim res As ProcessResult = Await RunProcessCaptureAsync(GetFFprobePath(), args, 9000, cts.Token)

                    If Not String.IsNullOrEmpty(res.StdOut) Then
                        Dim info As FFprobeOutput = JsonSerializer.Deserialize(Of FFprobeOutput)(res.StdOut, JsonOptions)
                        Dim durationCandidates As New List(Of TimeSpan)()

                        If info?.Format IsNot Nothing Then
                            Dim formatDur As TimeSpan = ParseAnyDuration(info.Format.Duration)
                            If formatDur > TimeSpan.Zero Then durationCandidates.Add(formatDur)

                            Dim br As Double
                            If Double.TryParse(info.Format.BitRate, NumberStyles.Any, CultureInfo.InvariantCulture, br) Then
                                bitrate = Math.Round(br / 1000).ToString(CultureInfo.InvariantCulture)
                            End If
                        End If

                        If info?.Streams IsNot Nothing Then
                            For Each stream In info.Streams
                                Dim streamDur As TimeSpan = ParseAnyDuration(stream.Duration)
                                If streamDur > TimeSpan.Zero Then durationCandidates.Add(streamDur)

                                If stream.Tags IsNot Nothing Then
                                    For Each tag In stream.Tags
                                        If tag.Key.Equals("DURATION", StringComparison.OrdinalIgnoreCase) Then
                                            Dim tagDur As TimeSpan = ParseAnyDuration(tag.Value)
                                            If tagDur > TimeSpan.Zero Then durationCandidates.Add(tagDur)
                                        End If
                                    Next
                                End If

                                If stream.CodecType = "audio" Then
                                    hasAudio = True
                                    If codec = "N/A" Then codec = If(stream.CodecName, "N/A")
                                ElseIf stream.CodecType = "video" Then
                                    hasVideo = True
                                    codec = If(stream.CodecName, "N/A")
                                    w = If(stream.Width, 0)
                                    h = If(stream.Height, 0)

                                    If Not String.IsNullOrEmpty(stream.RFrameRate) Then
                                        Dim parts() As String = stream.RFrameRate.Split("/"c)
                                        If parts.Length = 2 Then
                                            Dim num, den As Double
                                            If Double.TryParse(parts(0), NumberStyles.Any, CultureInfo.InvariantCulture, num) AndAlso
                                               Double.TryParse(parts(1), NumberStyles.Any, CultureInfo.InvariantCulture, den) AndAlso den > 0 Then
                                                fps = num / den
                                            End If
                                        Else
                                            Double.TryParse(stream.RFrameRate, NumberStyles.Any, CultureInfo.InvariantCulture, fps)
                                        End If
                                    End If
                                End If
                            Next
                        End If

                        If durationCandidates.Count > 0 Then
                            rawDuration = durationCandidates.Max()
                        End If
                    End If
                End Using

                Dim isActuallyStatic As Boolean = False
                If isStrictStaticImage Then isActuallyStatic = True

                Dim needsPacketScan As Boolean = False
                If Not isActuallyStatic Then
                    If isPotentialAnimatedImage OrElse rawDuration <= TimeSpan.FromSeconds(1.5) OrElse
                       (rawDuration = TimeSpan.Zero OrElse fps = 0) Then
                        needsPacketScan = True
                    End If
                End If

                If needsPacketScan Then
                    Try
                        Dim scanArgs As String = $"-v error -show_entries packet=pts_time -of csv=p=0 ""{filePath}"""
                        Using scanCts As New CancellationTokenSource(5000)
                            Dim scanRes As ProcessResult = Await RunProcessCaptureAsync(GetFFprobePath(), scanArgs, 4500, scanCts.Token)
                            If Not String.IsNullOrEmpty(scanRes.StdOut) Then
                                Dim maxPts As Double = 0.0
                                Dim packetCount As Integer = 0
                                Dim lines As String() = scanRes.StdOut.Split({vbCrLf, vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries)

                                For Each line In lines
                                    Dim ptsVal As Double
                                    If Double.TryParse(line.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, ptsVal) Then
                                        If ptsVal > maxPts Then maxPts = ptsVal
                                        packetCount += 1
                                    End If
                                Next

                                If isPotentialAnimatedImage Then
                                    If packetCount > 1 Then
                                        isActuallyStatic = False
                                        hasVideo = True

                                        Dim computedDur As TimeSpan = TimeSpan.FromSeconds(maxPts)
                                        If computedDur > rawDuration Then rawDuration = computedDur

                                        If rawDuration <= TimeSpan.Zero AndAlso packetCount > 1 Then
                                            rawDuration = TimeSpan.FromSeconds(packetCount / 30.0)
                                        End If

                                        If fps <= 0 AndAlso rawDuration.TotalSeconds > 0 Then
                                            fps = packetCount / rawDuration.TotalSeconds
                                        ElseIf fps <= 0 Then
                                            fps = 30.0
                                        End If
                                    Else
                                        isActuallyStatic = True
                                    End If
                                Else
                                    If Not isActuallyStatic AndAlso maxPts > 0.0 Then
                                        Dim computedDur As TimeSpan = TimeSpan.FromSeconds(maxPts)
                                        If computedDur > rawDuration Then
                                            rawDuration = computedDur
                                            If hasVideo AndAlso fps <= 0 Then fps = 30.0
                                        End If
                                    End If
                                End If
                            End If
                        End Using
                    Catch exScan As Exception
                        QueueLog($"[FFprobe] Ошибка при сканировании пакетов для {filePath}: {exScan.Message}")
                    End Try
                End If

                If isActuallyStatic Then
                    rawDuration = TimeSpan.Zero
                    fps = 0
                    hasAudio = False
                End If

                If Not isActuallyStatic Then
                    If hasVideo Then
                        If w <= 0 Then w = 640
                        If h <= 0 Then h = 480
                        w = (w \ 2) * 2
                        h = (h \ 2) * 2

                        If fps <= 0.1 OrElse Double.IsNaN(fps) OrElse Double.IsInfinity(fps) Then
                            fps = 30.0
                        End If
                    End If

                    If rawDuration <= TimeSpan.Zero Then
                        rawDuration = TimeSpan.FromSeconds(1.0)
                    End If
                End If

            Catch exProbe As Exception
                QueueLog($"[FFprobe] Ошибка базового парсинга для {filePath}: {exProbe.Message}")
            End Try

            Dim result As New MediaInfo With {
                .Duration = rawDuration,
                .Bitrate = bitrate,
                .Codec = codec,
                .Fps = fps,
                .Width = w,
                .Height = h,
                .HasAudio = hasAudio
            }

            If rawDuration > TimeSpan.Zero OrElse codec <> "N/A" Then
                mediaInfoCache.TryAdd(filePath, result)
            End If

            Return result

        Finally
            If lockAcquired Then refLock.Semaphore.Release()
            SyncLock _locksSyncObj
                refLock.RefCount -= 1
                If refLock.RefCount <= 0 Then
                    _mediaInfoLocks.Remove(filePath)
                    refLock.Semaphore.Dispose()
                End If
            End SyncLock
        End Try

        Return CreateDefaultMediaInfo()
    End Function

    Public Async Function RunProcessCaptureAsync(exePath As String, arguments As String, timeoutMs As Integer, token As CancellationToken) As Task(Of ProcessResult) Implements IFFmpegService.RunProcessCaptureAsync
        Dim isFFprobe As Boolean = exePath.Contains("ffprobe", StringComparison.OrdinalIgnoreCase)

        Using linkedCts As CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, _globalCts.Token)
            Dim activeToken As CancellationToken = linkedCts.Token

            If Not isFFprobe Then
                Await _processTaskRunner.WaitAsync(activeToken).ConfigureAwait(False)
            End If

            Try
                Dim startInfo As New ProcessStartInfo(exePath, arguments) With {
                    .UseShellExecute = False,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .CreateNoWindow = True,
                    .StandardOutputEncoding = Encoding.UTF8,
                    .StandardErrorEncoding = Encoding.UTF8
                }

                Using proc As New Process() With {.StartInfo = startInfo, .EnableRaisingEvents = True}
                    Dim tcsExit As New TaskCompletionSource(Of Boolean)()
                    AddHandler proc.Exited, Sub() tcsExit.TrySetResult(True)

                    Try
                        proc.Start()
                        RegisterProcess(proc)
                        Try
                            If Not proc.HasExited Then CreateJobForProcess(proc)
                        Catch exStart As InvalidOperationException
                        Catch exJobBind As Exception
                        End Try
                    Catch exStartError As Exception
                        Return New ProcessResult With {.ExitCode = -1, .StdOut = String.Empty, .StdErr = exStartError.Message}
                    End Try

                    Using timeoutCts As CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(activeToken)
                        timeoutCts.CancelAfter(timeoutMs)
                        Dim outTask As Task(Of String) = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token)
                        Dim errTask As Task(Of String) = proc.StandardError.ReadToEndAsync(timeoutCts.Token)

                        Try
                            Await Task.WhenAll(outTask, errTask, tcsExit.Task).WaitAsync(timeoutCts.Token).ConfigureAwait(False)

                            Dim rawErr As String = errTask.Result
                            Dim actualErrors As New StringBuilder()

                            If Not String.IsNullOrEmpty(rawErr) Then
                                Dim lines = rawErr.Split({vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries)
                                For Each line In lines
                                    If line.Contains("Error", StringComparison.OrdinalIgnoreCase) OrElse line.Contains("Failed", StringComparison.OrdinalIgnoreCase) Then
                                        actualErrors.AppendLine(line)
                                    ElseIf proc.ExitCode <> 0 AndAlso Not line.StartsWith("frame=") AndAlso Not line.StartsWith("size=") Then
                                        actualErrors.AppendLine(line)
                                    Else
                                        QueueLog($"[FFmpeg Info/Warn] {line}")
                                    End If
                                Next
                            End If

                            UnregisterProcess(proc)
                            Return New ProcessResult With {
                                .ExitCode = proc.ExitCode,
                                .StdOut = outTask.Result,
                                .StdErr = actualErrors.ToString().Trim()
                            }
                        Catch exCancel As OperationCanceledException
                            SafeKillProcess(proc)
                            UnregisterProcess(proc)
                            Return New ProcessResult With {.ExitCode = -1, .StdOut = String.Empty, .StdErr = If(activeToken.IsCancellationRequested, "Отменено пользователем или сервисом", "Таймаут выполнения")}
                        Catch exWait As Exception
                            SafeKillProcess(proc)
                            UnregisterProcess(proc)
                            Return New ProcessResult With {.ExitCode = -1, .StdOut = String.Empty, .StdErr = exWait.Message}
                        End Try
                    End Using
                End Using
            Finally
                If Not isFFprobe Then
                    _processTaskRunner.Release()
                End If
            End Try
        End Using
    End Function

    Private Shared Sub SafeKillProcess(proc As Process)
        If proc Is Nothing Then Return
        Try
            If Not proc.HasExited Then
                proc.Kill(entireProcessTree:=True)
                If Not proc.WaitForExit(2000) Then
                    Try
                        proc.Kill()
                    Catch
                    End Try
                End If
            End If
        Catch exInvalid As InvalidOperationException
        Catch exWin32 As ComponentModel.Win32Exception
        Catch ex As Exception
            System.Diagnostics.Debug.WriteLine($"[FFmpegService] Ошибка SafeKillProcess: {ex.Message}")
        Finally
            Try
                proc.Dispose()
            Catch
            End Try
        End Try
    End Sub

    Public Async Function StartFFmpegWithProgressAsync(arguments As String, targetDurationSec As Double, progressReporter As IProgress(Of FFmpegProgress), token As CancellationToken) As Task(Of Integer) Implements IFFmpegService.StartFFmpegWithProgressAsync
        Using linkedCts As CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, _globalCts.Token)
            Dim activeToken As CancellationToken = linkedCts.Token

            Await _processTaskRunner.WaitAsync(activeToken).ConfigureAwait(False)

            Try
                Dim ffmpegPath As String = GetFFmpegPath()
                Dim startInfo As New ProcessStartInfo() With {
                    .FileName = ffmpegPath,
                    .Arguments = arguments,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .UseShellExecute = False,
                    .CreateNoWindow = True
                }

                Using process As New Process() With {.StartInfo = startInfo}
                    Try
                        If Not process.Start() Then Return -1

                        ActiveFFmpegProcess = process
                        RegisterProcess(process)

                        Try
                            If Not process.HasExited Then CreateJobForProcess(process)
                        Catch exInvalid As InvalidOperationException
                        Catch exJobBind As Exception
                        End Try

                        Dim stdoutTask As Task = process.StandardOutput.ReadToEndAsync(activeToken)
                        Dim lastProgressUpdateTicks As Long = Environment.TickCount64

                        Try
                            Using reader As StreamReader = process.StandardError
                                While Not activeToken.IsCancellationRequested
                                    Dim line As String = Await reader.ReadLineAsync(activeToken).ConfigureAwait(False)
                                    If line Is Nothing Then Exit While

                                    If line.Contains("time=") Then
                                        Dim matchTime As Match = TimeRegex.Match(line)

                                        If matchTime.Success Then
                                            Dim isNegative As Boolean = matchTime.Groups(1).Value = "-"
                                            Dim hours As Integer = Integer.Parse(matchTime.Groups(2).Value, CultureInfo.InvariantCulture)
                                            Dim minutes As Integer = Integer.Parse(matchTime.Groups(3).Value, CultureInfo.InvariantCulture)
                                            Dim seconds As Integer = Integer.Parse(matchTime.Groups(4).Value, CultureInfo.InvariantCulture)

                                            Dim msRaw As String = matchTime.Groups(5).Value
                                            Dim msStr As String = If(String.IsNullOrEmpty(msRaw), "000", msRaw.PadRight(3, "0"c).AsSpan(0, 3).ToString())
                                            Dim milliseconds As Integer = Integer.Parse(msStr, CultureInfo.InvariantCulture)

                                            Dim totalSeconds As Double = hours * 3600.0 + minutes * 60.0 + seconds + milliseconds / 1000.0
                                            Dim current As TimeSpan = If(isNegative, TimeSpan.Zero, TimeSpan.FromSeconds(totalSeconds))

                                            If targetDurationSec > 0 AndAlso current <> TimeSpan.Zero Then
                                                Dim rawPercentage As Integer = CInt((current.TotalSeconds / targetDurationSec) * 100)
                                                Dim safePercentage As Integer = Math.Clamp(rawPercentage, 0, 100)

                                                Dim currentTicks As Long = Environment.TickCount64

                                                If (currentTicks - Volatile.Read(lastProgressUpdateTicks)) > 200 Then
                                                    Volatile.Write(lastProgressUpdateTicks, currentTicks)

                                                    Dim currentSpeed As Double = 1.0

                                                    If line.Contains("speed=") Then
                                                        Dim matchSpeed As Match = SpeedRegex.Match(line)
                                                        If matchSpeed.Success Then
                                                            Dim parsedSpeed As Double
                                                            If Double.TryParse(matchSpeed.Groups(1).Value, NumberStyles.Any, CultureInfo.InvariantCulture, parsedSpeed) Then
                                                                currentSpeed = parsedSpeed
                                                            End If
                                                        End If
                                                    End If

                                                    Dim timeRemaining As TimeSpan = TimeSpan.Zero

                                                    If safePercentage > 0 Then
                                                        Dim elapsedSec As Double = current.TotalSeconds
                                                        Dim totalEstimatedSec As Double = (elapsedSec / safePercentage) * 100
                                                        Dim remainingSec As Double = totalEstimatedSec - elapsedSec

                                                        If remainingSec > 0 Then
                                                            timeRemaining = TimeSpan.FromSeconds(remainingSec)
                                                        End If
                                                    End If

                                                    Dim progressReport As New FFmpegProgress With {
                                                        .ProgressPercentage = safePercentage,
                                                        .TimeRemaining = timeRemaining,
                                                        .AverageSpeed = currentSpeed,
                                                        .Message = $"Обработка: {safePercentage}%"
                                                    }
                                                    progressReporter.Report(progressReport)
                                                End If
                                            End If
                                        End If
                                    End If
                                End While
                            End Using
                        Catch exProgress As Exception
                            Return -1
                        End Try

                        Await Task.WhenAll(process.WaitForExitAsync(activeToken), stdoutTask).ConfigureAwait(False)
                        UnregisterProcess(process)
                        Return process.ExitCode
                    Catch ex As OperationCanceledException
                        SafeKillProcess(process)
                        UnregisterProcess(process)
                        Return -1
                    Catch ex As Exception
                        SafeKillProcess(process)
                        UnregisterProcess(process)
                        Return -1
                    End Try
                End Using
            Catch ex As OperationCanceledException
                Return -1
            Catch ex As Exception
                Return -1
            Finally
                ActiveFFmpegProcess = Nothing
                _processTaskRunner.Release()
            End Try
        End Using
    End Function

    Private Const JobObjectExtendedLimitInformation As Integer = 9
    Private Const JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE As UInteger = &H2000

    <DllImport("kernel32.dll", SetLastError:=True, CharSet:=CharSet.Unicode)>
    Private Shared Function CreateJobObject(lpJobAttributes As IntPtr, lpName As String) As IntPtr
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function AssignProcessToJobObject(hJob As IntPtr, hProcess As IntPtr) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function SetInformationJobObject(hJob As IntPtr, JobObjectInformationClass As Integer, lpJobObjectInformation As IntPtr, cbJobObjectInformationLength As Integer) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CloseHandle(hObject As IntPtr) As Boolean
    End Function

    <StructLayout(LayoutKind.Sequential)>
    Private Structure JOBOBJECT_BASIC_LIMIT_INFORMATION
        Public PerProcessUserTimeLimit, PerJobUserTimeLimit As Long
        Public LimitFlags As UInteger
        Public MinimumWorkingSetSize, MaximumWorkingSetSize As UIntPtr
        Public ActiveProcessLimit As UInteger
        Public Affinity As UIntPtr
        Public PriorityClass, SchedulingClass As UInteger
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure IO_COUNTERS
        Public ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount As ULong
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        Public BasicLimitInformation As JOBOBJECT_BASIC_LIMIT_INFORMATION
        Public IoInfo As IO_COUNTERS
        Public ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed As UIntPtr
    End Structure

    Private Sub CreateJobForProcess(p As Process)
        If p Is Nothing OrElse _jobHandle = IntPtr.Zero Then Return

        Try
            If p.HasExited Then Return

            Dim procHandle As IntPtr = p.Handle
            If Not AssignProcessToJobObject(_jobHandle, procHandle) Then
                Dim errCode As Integer = Marshal.GetLastWin32Error()
                QueueLog($"Ошибка привязки процесса к JobObject. Код ошибки Windows: {errCode}")
            End If
        Catch exInvalid As InvalidOperationException
            QueueLog($"InvalidOperationException при привязке JobObject: {exInvalid.Message}")
        Catch ex As Exception
            QueueLog($"Неизвестная ошибка при привязке JobObject: {ex.Message}")
        End Try
    End Sub

    Public Async Function ExtractAudioToWavAsync(inputPath As String, outputPath As String, token As CancellationToken) As Task(Of Boolean) Implements IFFmpegService.ExtractAudioToWavAsync
        Dim ffmpegPath As String = GetFFmpegPath()
        Dim args As String = $"-hide_banner -loglevel error -y -i ""{inputPath}"" -vn -c:a pcm_s16le -ar 48000 -ac 2 ""{outputPath}"""
        Dim result = Await RunProcessCaptureAsync(ffmpegPath, args, 60000, token)
        Return result.ExitCode = 0 AndAlso IO.File.Exists(outputPath)
    End Function

    Public Async Function GenerateAudioPeaksAsync(inputPath As String, samplesPerPeak As Integer, token As CancellationToken) As Task(Of IServices.PeakMinMax()) Implements IFFmpegService.GenerateAudioPeaksAsync
        Dim ffmpegPath As String = GetFFmpegPath()
        Dim args As String = $"-hide_banner -loglevel error -i ""{inputPath}"" -vn -f s16le -acodec pcm_s16le -ar 48000 -ac 2 -"

        Dim psi As New ProcessStartInfo() With {
            .FileName = ffmpegPath,
            .Arguments = args,
            .UseShellExecute = False,
            .RedirectStandardOutput = True,
            .CreateNoWindow = True
        }

        Dim peaks As New List(Of IServices.PeakMinMax)()

        Using linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, _globalCts.Token)
            Dim activeToken = linkedCts.Token
            Using proc As New Process() With {.StartInfo = psi}
                Try
                    proc.Start()
                    RegisterProcess(proc)

                    Dim bytesPerPeak As Integer = samplesPerPeak * 4 ' 4 bytes per stereo sample (16-bit)
                    Dim buffer(bytesPerPeak * 100 - 1) As Byte
                    Dim stdout = proc.StandardOutput.BaseStream

                    Dim currentMinL As Short = Short.MaxValue, currentMaxL As Short = Short.MinValue
                    Dim currentMinR As Short = Short.MaxValue, currentMaxR As Short = Short.MinValue
                    Dim samplesCount As Integer = 0

                    While Not activeToken.IsCancellationRequested
                        Dim bytesRead As Integer = Await stdout.ReadAsync(buffer, activeToken).ConfigureAwait(False)
                        If bytesRead = 0 Then Exit While

                        For i As Integer = 0 To bytesRead - 1 Step 4
                            If i + 3 >= bytesRead Then Exit For

                            Dim left As Short = BitConverter.ToInt16(buffer, i)
                            Dim right As Short = BitConverter.ToInt16(buffer, i + 2)

                            If left < currentMinL Then currentMinL = left
                            If left > currentMaxL Then currentMaxL = left
                            If right < currentMinR Then currentMinR = right
                            If right > currentMaxR Then currentMaxR = right

                            samplesCount += 1
                            If samplesCount >= samplesPerPeak Then
                                peaks.Add(New IServices.PeakMinMax With {
                                    .MinL = CSByte(currentMinL \ 256),
                                    .MaxL = CSByte(currentMaxL \ 256),
                                    .MinR = CSByte(currentMinR \ 256),
                                    .MaxR = CSByte(currentMaxR \ 256)
                                })
                                currentMinL = Short.MaxValue : currentMaxL = Short.MinValue
                                currentMinR = Short.MaxValue : currentMaxR = Short.MinValue
                                samplesCount = 0
                            End If
                        Next
                    End While
                Catch ex As Exception
                    System.Diagnostics.Debug.WriteLine($"[PeakGenerator] Ошибка генерации пиков: {ex.Message}")
                Finally
                    Try
                        If Not proc.HasExited Then proc.Kill()
                    Catch
                    End Try
                    UnregisterProcess(proc)
                End Try
            End Using
        End Using

        Return peaks.ToArray()
    End Function

    Private _disposedValue As Boolean = False

    Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
        GC.SuppressFinalize(Me)
        If _disposedValue Then Return New ValueTask()
        Return New ValueTask(DisposeAsyncCore())
    End Function

    Private Async Function DisposeAsyncCore() As Task
        If Not _globalCts.IsCancellationRequested Then
            _globalCts.Cancel()
        End If

        For Each proc In _activeProcesses.Keys
            SafeKillProcess(proc)
        Next
        _activeProcesses.Clear()

        Await Task.Delay(50).ConfigureAwait(False)

        Try
            _globalCts.Dispose()
            _processTaskRunner?.Dispose()
        Catch
        End Try

        Dispose(False)
    End Function

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not _disposedValue Then
            If disposing Then
                If Not _globalCts.IsCancellationRequested Then
                    _globalCts.Cancel()
                End If

                For Each proc In _activeProcesses.Keys
                    SafeKillProcess(proc)
                Next
                _activeProcesses.Clear()

                Try
                    _globalCts.Dispose()
                    _processTaskRunner?.Dispose()
                Catch
                End Try
            End If

            If _jobHandle <> IntPtr.Zero Then
                Try
                    CloseHandle(_jobHandle)
                Catch
                End Try
            End If

            _disposedValue = True
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(True)
        GC.SuppressFinalize(Me)
    End Sub

    Protected Overrides Sub Finalize()
        Dispose(False)
    End Sub

    Public Class FFprobeOutput
        <JsonPropertyName("streams")> Public Property Streams As FFprobeStream()
        <JsonPropertyName("format")> Public Property Format As FFprobeFormat
    End Class

    Public Class FFprobeStream
        <JsonPropertyName("codec_type")> Public Property CodecType As String
        <JsonPropertyName("codec_name")> Public Property CodecName As String
        <JsonPropertyName("width")> Public Property Width As Integer?
        <JsonPropertyName("height")> Public Property Height As Integer?
        <JsonPropertyName("r_frame_rate")> Public Property RFrameRate As String
        <JsonPropertyName("duration")> Public Property Duration As String
        <JsonPropertyName("tags")> Public Property Tags As Dictionary(Of String, String)
    End Class

    Public Class FFprobeFormat
        <JsonPropertyName("duration")> Public Property Duration As String
        <JsonPropertyName("bit_rate")> Public Property BitRate As String
    End Class

End Class