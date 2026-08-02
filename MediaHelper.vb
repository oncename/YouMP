Option Strict On
Option Explicit On

Imports System.IO
Imports System.Diagnostics
Imports System.Globalization
Imports System.Threading
Imports System.Threading.Tasks
Imports NReco.VideoInfo

Public Class MediaHelper
    Public Shared Async Function GetRealMediaDurationAsync(filePath As String,
                                                Optional ffmpegBinPath As String = Nothing) As Task(Of TimeSpan)
        If String.IsNullOrEmpty(filePath) OrElse Not File.Exists(filePath) Then
            Return TimeSpan.Zero
        End If

        If String.IsNullOrEmpty(ffmpegBinPath) Then
            ffmpegBinPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "bin")
        End If

        Dim ffProbe As New FFProbe() With {.ToolPath = ffmpegBinPath}

        Try
            Dim mediaInfo = ffProbe.GetMediaInfo(filePath)
            Dim containerDuration As TimeSpan = mediaInfo.Duration
            Dim hasVideo As Boolean = False
            Dim isVp9 As Boolean = False

            If mediaInfo.Streams IsNot Nothing Then
                For Each stream In mediaInfo.Streams
                    If String.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase) Then
                        hasVideo = True
                        If String.Equals(stream.CodecName, "vp9", StringComparison.OrdinalIgnoreCase) Then
                            isVp9 = True
                        End If
                    End If
                Next
            End If

            Dim ext As String = Path.GetExtension(filePath).ToLowerInvariant()
            Dim isWebmContainer As Boolean = (ext = ".webm" OrElse ext = ".mkv")

            If hasVideo AndAlso (containerDuration < TimeSpan.FromSeconds(1.5) OrElse
                                 (isWebmContainer AndAlso isVp9)) Then
                Dim trueStreamDuration As TimeSpan = Await GetTrueStreamDurationViaFFprobeAsync(ffmpegBinPath, filePath)
                If trueStreamDuration > TimeSpan.Zero Then
                    Return trueStreamDuration
                End If
            End If

            Return containerDuration
        Catch ex As Exception
            Debug.WriteLine($"[MediaHelper] Ошибка: {ex.Message}")
        End Try

        Return TimeSpan.Zero
    End Function

    Private Shared Async Function GetTrueStreamDurationViaFFprobeAsync(binPath As String, filePath As String) As Task(Of TimeSpan)
        Dim ffprobeExe As String = Path.Combine(binPath, "ffprobe.exe")
        If Not File.Exists(ffprobeExe) Then ffprobeExe = "ffprobe.exe"

        Try
            Dim args As String = $"-v error -select_streams v:0 -show_entries stream=duration:stream_tags=DURATION -of default=noprint_wrappers=0 ""{filePath}"""
            Dim startInfo As New ProcessStartInfo() With {
                .FileName = ffprobeExe,
                .Arguments = args,
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True, ' ИСПРАВЛЕНИЕ: Обязательно редиректим Error, чтобы буфер не переполнялся и не вешал процесс
                .CreateNoWindow = True
            }

            Using proc As New Process() With {.StartInfo = startInfo}
                proc.Start()

                Dim output As String = String.Empty

                ' ИСПРАВЛЕНИЕ: Создаем токен с таймаутом на 3000 мс
                Using cts As New CancellationTokenSource(3000)
                    ' Жесткий килл процесса на уровне ОС при истечении времени
                    Using cts.Token.Register(Sub()
                                                 Try
                                                     If Not proc.HasExited Then
                                                         proc.Kill()
                                                     End If
                                                 Catch
                                                 End Try
                                             End Sub)

                        Dim readOutputTask = proc.StandardOutput.ReadToEndAsync()
                        Dim readErrorTask = proc.StandardError.ReadToEndAsync()

                        ' Ждем физического завершения (с таймаутом), не блокируя поток UI
                        Dim exited = Await Task.Run(Function() proc.WaitForExit(3000))

                        If Not exited Then
                            Try
                                proc.Kill()
                            Catch
                            End Try
                            Debug.WriteLine($"[MediaHelper] ffprobe завис и был принудительно убит (Таймаут).")
                        Else
                            ' Процесс завершился вовремя, теперь безопасно читаем вывод
                            output = Await readOutputTask
                        End If
                    End Using
                End Using

                If Not String.IsNullOrEmpty(output) Then
                    For Each line In output.Split({vbCrLf, vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries)
                        Dim trimmed As String = line.Trim()
                        If trimmed.StartsWith("TAG:DURATION=", StringComparison.OrdinalIgnoreCase) Then
                            Dim val As String = trimmed.Substring(13).Trim()
                            Dim ts As TimeSpan
                            If TimeSpan.TryParse(val, CultureInfo.InvariantCulture, ts) Then Return ts
                        End If
                        If trimmed.StartsWith("duration=", StringComparison.OrdinalIgnoreCase) Then
                            Dim val As String = trimmed.Substring(9).Trim()
                            Dim seconds As Double
                            If Double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, seconds) AndAlso seconds > 0 Then
                                Return TimeSpan.FromSeconds(seconds)
                            End If
                        End If
                    Next
                End If
            End Using
        Catch ex As Exception
            Debug.WriteLine($"[MediaHelper] Ошибка извлечения тегов: {ex.Message}")
        End Try

        Return TimeSpan.Zero
    End Function
End Class