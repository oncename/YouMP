Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports LibreHardwareMonitor.Hardware

Public Class HardwareMonitorService
    Implements IServices.IHardwareMonitorService

    Public Event LogMessage As Action(Of String)

    Private ReadOnly _computer As Computer
    Private _disposedValue As Boolean

    ' Флаги и объект синхронизации для предотвращения утечек WMI
    Private _isOpen As Boolean = False
    Private ReadOnly _openLock As New Object()

    Public Class HardwareScanResult
        Public Property AllItems As New List(Of String)()
        Public Property FoundGpuName As String = String.Empty
        Public Property FoundCpuName As String = "Unknown CPU"
        Public Property IsNvidiaGpu As Boolean = False
        Public Property IsAmdGpu As Boolean = False
        Public Property IsCpuOnly As Boolean = True

        Public ReadOnly Property ActiveHardwareName As String
            Get
                If IsCpuOnly OrElse String.IsNullOrEmpty(FoundGpuName) Then
                    Return FoundCpuName
                Else
                    Return FoundGpuName
                End If
            End Get
        End Property
    End Class

    Public Sub New()
        _computer = New Computer With {
            .IsCpuEnabled = True,
            .IsGpuEnabled = True
        }
    End Sub

    Public Async Function ScanHardwareAsync() As Task(Of HardwareScanResult) Implements IServices.IHardwareMonitorService.ScanHardwareAsync
        Dim result As New HardwareScanResult()

        Try
            Await Task.Run(Sub()
                               Try
                                   SyncLock _openLock
                                       If Not _isOpen Then
                                           _computer.Open()
                                           _isOpen = True
                                       End If
                                   End SyncLock

                                   ' 1. Сбор сырых данных из WMI
                                   For Each hardwareItem As IHardware In _computer.Hardware
                                       If hardwareItem.HardwareType = HardwareType.Cpu Then
                                           Dim cpuName As String = hardwareItem.Name
                                           If Not String.IsNullOrEmpty(cpuName) Then
                                               result.FoundCpuName = cpuName.Trim()
                                           End If
                                       End If

                                       If hardwareItem.HardwareType = HardwareType.GpuNvidia OrElse hardwareItem.HardwareType = HardwareType.GpuAmd Then
                                           Dim hwName As String = hardwareItem.Name
                                           If Not String.IsNullOrEmpty(hwName) Then
                                               result.FoundGpuName = hwName.Trim()

                                               If hardwareItem.HardwareType = HardwareType.GpuNvidia Then
                                                   result.IsNvidiaGpu = True
                                                   result.IsCpuOnly = False
                                               ElseIf hardwareItem.HardwareType = HardwareType.GpuAmd Then
                                                   result.IsAmdGpu = True
                                                   result.IsCpuOnly = False
                                               End If
                                           End If
                                       End If
                                   Next

                                   ' 2. Системный фолбек для CPU (если LibreHardwareMonitor запущен без админа)
                                   If String.IsNullOrEmpty(result.FoundCpuName) OrElse result.FoundCpuName = "Unknown CPU" Then
                                       result.FoundCpuName = GetProcessorNameFromSystem()
                                   End If

                                   ' Подмена для логики UI, если нет видеокарты
                                   If result.IsCpuOnly Then
                                       result.FoundGpuName = result.FoundCpuName
                                   End If

                                   ' =========================================================
                                   ' 3. СТРОГОЕ ФОРМИРОВАНИЕ СПИСКА (для ComboBox2)
                                   ' =========================================================
                                   result.AllItems.Clear()

                                   ' СНАЧАЛА ВИДЕОКАРТА - добавляем строго первой (если она есть)
                                   If Not result.IsCpuOnly AndAlso Not String.IsNullOrEmpty(result.FoundGpuName) Then
                                       result.AllItems.Add(result.FoundGpuName)
                                   End If

                                   ' ЗАТЕМ ПРОЦЕССОР - добавляем строго вторым
                                   If Not String.IsNullOrEmpty(result.FoundCpuName) AndAlso result.FoundCpuName <> "Unknown CPU" Then
                                       ' Защита от дубликатов (ничего лишнего)
                                       If Not result.AllItems.Contains(result.FoundCpuName) Then
                                           result.AllItems.Add(result.FoundCpuName)
                                       End If
                                   End If

                               Catch ex As Exception
                                   RaiseEvent LogMessage($"[HardwareMonitor ERROR] Ошибка при сканировании оборудования: {ex.Message}")
                               End Try
                           End Sub)
        Catch ex As Exception
            RaiseEvent LogMessage($"[HardwareMonitor ERROR] Ошибка запуска задачи сканирования: {ex.Message}")
        End Try

        Return result
    End Function

    ''' <summary>
    ''' Определяет поколение видеокарты NVIDIA
    ''' </summary>
    Public Function GetNvidiaGeneration(gpuName As String) As Integer Implements IServices.IHardwareMonitorService.GetNvidiaGeneration
        If String.IsNullOrEmpty(gpuName) Then Return 0

        Dim matchObj As Match = Regex.Match(gpuName, "(?:RTX|GTX|GTS|GT)\s*(?:M\s*)?(\d{3,4})\b", RegexOptions.IgnoreCase)
        If matchObj.Success Then
            Dim modelNum As Integer
            If Integer.TryParse(matchObj.Groups(1).Value, modelNum) Then
                If modelNum >= 1000 Then Return modelNum \ 100
                Return modelNum \ 100
            End If
        End If

        Dim genericMatch As Match = Regex.Match(gpuName, "\b(\d{3,4})\b")
        If genericMatch.Success Then
            Dim modelNum As Integer
            If Integer.TryParse(genericMatch.Groups(1).Value, modelNum) Then
                Return modelNum \ 100
            End If
        End If

        Return 0
    End Function

    ''' <summary>
    ''' Определяет поколение видеокарты AMD
    ''' </summary>
    Public Function GetAmdGeneration(gpuName As String) As Integer Implements IServices.IHardwareMonitorService.GetAmdGeneration
        If String.IsNullOrEmpty(gpuName) Then Return 0

        Dim matchObj As Match = Regex.Match(gpuName, "(?:RX|HD|R9|R7)\s*(?:M\s*)?(\d{3,4})\b", RegexOptions.IgnoreCase)
        If matchObj.Success Then
            Dim modelNum As Integer
            If Integer.TryParse(matchObj.Groups(1).Value, modelNum) Then
                If modelNum >= 1000 Then Return (modelNum \ 1000) * 10
                Return modelNum \ 100
            End If
        End If

        Dim genericMatch As Match = Regex.Match(gpuName, "\b(\d{3,4})\b")
        If genericMatch.Success Then
            Dim modelNum As Integer
            If Integer.TryParse(genericMatch.Groups(1).Value, modelNum) Then
                If modelNum >= 1000 Then Return (modelNum \ 1000) * 10
                Return modelNum \ 100
            End If
        End If

        Return 0
    End Function

    Private Shared Function GetProcessorNameFromSystem() As String
        Try
            Using key As Microsoft.Win32.RegistryKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("HARDWARE\DESCRIPTION\System\CentralProcessor\0")
                If key IsNot Nothing Then
                    Dim nameObj As Object = key.GetValue("ProcessorNameString")
                    If nameObj IsNot Nothing Then Return nameObj.ToString().Trim()
                End If
            End Using
        Catch ex As Exception
            System.Diagnostics.Debug.WriteLine($"[HardwareMonitor] Ошибка чтения имени процессора из реестра: {ex.Message}")
        End Try

        Try
            Dim envIdentifier As String = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
            If Not String.IsNullOrEmpty(envIdentifier) Then Return envIdentifier.Trim()
        Catch ex As Exception
            System.Diagnostics.Debug.WriteLine($"[HardwareMonitor] Ошибка чтения PROCESSOR_IDENTIFIER: {ex.Message}")
        End Try

        Return "Intel Core i5-13600KF"
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(True)
        GC.SuppressFinalize(Me)
    End Sub

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not _disposedValue Then
            If disposing Then
                If _computer IsNot Nothing Then
                    Try
                        ' ИСПРАВЛЕНИЕ: Удален Task.Run. Освобождение ресурсов выполняется синхронно.
                        SyncLock _openLock
                            If _isOpen Then
                                _computer.Close()
                                _isOpen = False
                            End If
                        End SyncLock
                    Catch ex As Exception
                        RaiseEvent LogMessage($"[HardwareMonitor ERROR] Ошибка при закрытии WMI: {ex.Message}")
                    End Try
                End If
            End If
            _disposedValue = True
        End If
    End Sub
End Class