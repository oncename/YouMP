Option Strict On
Option Explicit On

Imports System.IO
Imports System.Text
Imports System.Linq
Imports System.Security.Cryptography

Public Class FileManager
    Private ReadOnly _startupPath As String
    Private ReadOnly _cacheDir As String

    Public Event LogMessage As Action(Of String)

    Public Sub New(startupPath As String)
        _startupPath = startupPath
        _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yoump", "Cache")
        Try
            If Not Directory.Exists(_cacheDir) Then Directory.CreateDirectory(_cacheDir)
        Catch ex As Exception
            RaiseEvent LogMessage("Ошибка создания директории кэша: " & ex.Message)
        End Try
    End Sub

    Public Function GetCacheFilePath(originalFilePath As String, customKey As String, prefix As String, extension As String) As String
        Dim fileInfo As New FileInfo(originalFilePath)
        Dim hashInput As String

        If fileInfo.Exists Then
            Dim fileHeaderHash As String = ""
            Try
                ' Использование CLng() и изменение порядка, чтобы предотвратить OverflowException
                Using fs As New FileStream(originalFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096)
                    Dim chunkSize As Integer = 8192
                    ' Сначала находим минимум в Long, потом конвертируем в Integer
                    Dim bufferSize As Integer = CInt(Math.Min(fs.Length, CLng(chunkSize * 2)))
                    Dim buffer(bufferSize - 1) As Byte

                    Dim bytesReadStart As Integer = fs.Read(buffer, 0, CInt(Math.Min(CLng(chunkSize), fs.Length)))
                    Dim bytesReadEnd As Integer = 0

                    If fs.Length > chunkSize Then
                        ' Сдвигаем указатель в конец
                        fs.Seek(Math.Max(0L, fs.Length - chunkSize), SeekOrigin.Begin)
                        ' Читаем конец файла с безопасным приведением
                        bytesReadEnd = fs.Read(buffer, bytesReadStart, CInt(Math.Min(CLng(chunkSize), fs.Length - chunkSize)))
                    End If

                    Dim totalBytesRead As Integer = bytesReadStart + bytesReadEnd
                    If totalBytesRead > 0 Then
                        Dim headerHashBytes = System.IO.Hashing.XxHash64.Hash(buffer.AsSpan(0, totalBytesRead))
                        fileHeaderHash = Convert.ToHexStringLower(headerHashBytes)
                    End If
                End Using
            Catch ex As Exception
                fileHeaderHash = "locked_" & Guid.NewGuid().ToString("N")
            End Try

            hashInput = $"{originalFilePath}_{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}_{fileHeaderHash}_{customKey}"
        Else
            hashInput = $"{originalFilePath}_missing_{Guid.NewGuid():N}_{customKey}"
        End If

        Dim inputBytes As Byte() = Encoding.UTF8.GetBytes(hashInput)
        Dim hashBytes As Byte() = System.IO.Hashing.XxHash64.Hash(inputBytes)

        Return Path.Combine(_cacheDir, $"{prefix}_{Convert.ToHexStringLower(hashBytes)}{extension}")
    End Function

    Public Function GetDownloadsDirectory(ByRef warningMessage As String) As String
        warningMessage = String.Empty
        Dim currentDir As String = SettingsService.Instance.Current.DownloadsDirectory
        Dim defaultDir As String = Path.Combine(_startupPath, "downloads")

        Try
            If String.IsNullOrWhiteSpace(currentDir) Then
                currentDir = defaultDir
            End If

            If Not Directory.Exists(currentDir) Then
                Try
                    Directory.CreateDirectory(currentDir)
                Catch ex As Exception
                    warningMessage = $"Папка не существует и её не удалось создать: {currentDir}. Используется папка по умолчанию."
                    If Not Directory.Exists(defaultDir) Then Directory.CreateDirectory(defaultDir)
                    Return defaultDir
                End Try
            End If

            Return currentDir
        Catch ex As Exception
            warningMessage = "Ошибка при определении папки загрузок: " & ex.Message
            Try
                If Not Directory.Exists(defaultDir) Then Directory.CreateDirectory(defaultDir)
            Catch
            End Try
            Return defaultDir
        End Try
    End Function

    Public Sub ClearAllTemporaryFiles()
        Try
            If Directory.Exists(_cacheDir) Then
                Dim cacheFiles As String() = Directory.GetFiles(_cacheDir)
                For Each filePath As String In cacheFiles
                    SafeDeleteFile(filePath)
                Next
            End If
        Catch ex As Exception
            RaiseEvent LogMessage("Ошибка при чтении директории кэша для очистки: " & ex.Message)
        End Try

        Try
            Dim tempDir As String = Path.GetTempPath()
            If Directory.Exists(tempDir) Then
                Dim tempStripFiles1 As String() = Directory.GetFiles(tempDir, "strip1_*.jpg")
                For Each filePath As String In tempStripFiles1
                    SafeDeleteFile(filePath)
                Next

                Dim tempStripFiles2 As String() = Directory.GetFiles(tempDir, "strip2_*.jpg")
                For Each filePath As String In tempStripFiles2
                    SafeDeleteFile(filePath)
                Next

                ' Очистка зависших запеченных аудиофайлов
                Dim bakedAudioFiles As String() = Directory.GetFiles(tempDir, "baked_audio_*.wav")
                For Each filePath As String In bakedAudioFiles
                    SafeDeleteFile(filePath)
                Next
            End If
        Catch ex As Exception
            RaiseEvent LogMessage("Ошибка при очистке системной папки Temp: " & ex.Message)
        End Try
    End Sub

    Private Sub SafeDeleteFile(filePath As String)
        Try
            File.Delete(filePath)
        Catch ex As Exception
            RaiseEvent LogMessage($"Не удалось удалить файл {filePath}: {ex.Message}")
        End Try
    End Sub

    Public Shared Function ParseInputFiles(inputText As String) As List(Of String)
        If String.IsNullOrWhiteSpace(inputText) Then Return New List(Of String)()
        Dim pathStr As String = inputText.Trim().Trim(""""c)
        Dim isBluray As Boolean = pathStr.StartsWith("bluray:", StringComparison.OrdinalIgnoreCase)
        If isBluray OrElse File.Exists(pathStr) Then Return New List(Of String) From {pathStr}
        Dim separators As String() = {vbCrLf, vbLf}
        Dim lines As String() = inputText.Split(separators, StringSplitOptions.RemoveEmptyEntries)
        Return lines.Select(Function(l) l.Trim().Trim(""""c)).Where(Function(l) File.Exists(l)).Distinct().ToList()
    End Function
End Class