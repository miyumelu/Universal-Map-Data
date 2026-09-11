Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json
Imports SixLabors.ImageSharp

Public NotInheritable Class Packer

    Private Const MAGIC As String = "CMD2"
    Private Const VERSION As Byte = 2
    Private Const HEADER_SIZE As Integer = 64
    Private Const ENTRY_SIZE As Integer = 26
    Private Const BYTES_PER_PIXEL As Integer = 4
    Private Const COMPRESSION_MODE_PNG_GZIP As Byte = 3

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function GetSystemFileCacheSize(
        ByRef lpMinimumFileCacheSize As IntPtr,
        ByRef lpMaximumFileCacheSize As IntPtr,
        ByRef lpFlags As UInteger) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function SetSystemFileCacheSize(
        MinimumFileCacheSize As IntPtr,
        MaximumFileCacheSize As IntPtr,
        Flags As UInteger) As Boolean
    End Function

    Private Const FILE_CACHE_MAX_HARD_ENABLE As UInteger = 1

    Public Shared Sub RunWithLimitedFileCache(maxCacheBytes As Long, action As Action)
        Dim origMin As IntPtr, origMax As IntPtr, origFlags As UInteger
        Dim gotOriginal = GetSystemFileCacheSize(origMin, origMax, origFlags)

        Dim limited = False
        Try
            limited = SetSystemFileCacheSize(
                New IntPtr(-1), New IntPtr(maxCacheBytes), FILE_CACHE_MAX_HARD_ENABLE)
        Catch
            limited = False
        End Try

        Try
            action()
        Finally
            If limited AndAlso gotOriginal Then
                Try
                    SetSystemFileCacheSize(origMin, origMax, origFlags)
                Catch

                End Try
            End If
        End Try
    End Sub

    Public Shared Sub Pack(dayMapFolder As String, outputCmdPath As String,
                            Optional progress As Action(Of String) = Nothing,
                            Optional gzipLevel As CompressionLevel = CompressionLevel.Optimal,
                            Optional scanDegreeOfParallelism As Integer = 0,
                            Optional encodeDegreeOfParallelism As Integer = 0)

        If scanDegreeOfParallelism <= 0 Then scanDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 2, 16)
        If encodeDegreeOfParallelism <= 0 Then encodeDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8)

        Dim jsonPath = Path.Combine(dayMapFolder, "TileMapInfo.json")
        If Not File.Exists(jsonPath) Then
            Throw New FileNotFoundException("TileMapInfo.json not found.", jsonPath)
        End If
        Dim jsonBytes = Encoding.UTF8.GetBytes(File.ReadAllText(jsonPath))

        Dim tilesRoot = Path.Combine(dayMapFolder, "Tiles")
        If Not Directory.Exists(tilesRoot) Then
            Throw New DirectoryNotFoundException("Tiles-Directory not found: " & tilesRoot)
        End If

        Dim xDirEntries As New List(Of (ZVal As Integer, XDir As String))()
        For Each zDir In Directory.EnumerateDirectories(tilesRoot)
            Dim zVal As Integer
            If Not Integer.TryParse(Path.GetFileName(zDir), zVal) Then Continue For
            For Each xDir In Directory.EnumerateDirectories(zDir)
                xDirEntries.Add((zVal, xDir))
            Next
        Next

        Dim scanParallelOptions As New ParallelOptions With {
            .MaxDegreeOfParallelism = scanDegreeOfParallelism
        }

        Dim totalCount = 0
        Dim probeTilePath As String = Nothing
        Dim probeLock As New Object()
        Dim countSw = Stopwatch.StartNew()

        Parallel.ForEach(xDirEntries, scanParallelOptions,
            Sub(item)
                Dim xVal As Integer
                If Not Integer.TryParse(Path.GetFileName(item.XDir), xVal) Then Return

                For Each pngFile In Directory.EnumerateFiles(item.XDir, "*.png")
                    Dim nameNoExt = Path.GetFileNameWithoutExtension(pngFile)
                    Dim yVal As Integer
                    If Not Integer.TryParse(nameNoExt, yVal) Then Continue For

                    If probeTilePath Is Nothing Then
                        SyncLock probeLock
                            If probeTilePath Is Nothing Then probeTilePath = pngFile
                        End SyncLock
                    End If

                    Dim done = Interlocked.Increment(totalCount)
                    If progress IsNot Nothing AndAlso (done Mod 5000 = 0) Then
                        Dim rate = done / countSw.Elapsed.TotalSeconds
                        progress($"SCAN: {done} Tiles counted – {rate:N0} F/s …")
                    End If
                Next
            End Sub)

        countSw.Stop()

        If totalCount = 0 Then
            Throw New InvalidOperationException("No PNG-Tiles found under " & tilesRoot)
        End If

        progress?.Invoke($"SCAN FINISHED: {totalCount} Tiles found in {countSw.Elapsed.TotalSeconds:N1}s")
        progress?.Invoke($"0 % – 0 / {totalCount} Tiles …")

        Dim overlaysJsonPath = Path.Combine(dayMapFolder, "Overlays.json")
        Dim overlaysDir = Path.Combine(dayMapFolder, "Overlays")
        Dim hasOverlays = File.Exists(overlaysJsonPath)
        Dim overlaysJsonBytes As Byte() = Nothing
        Dim iconEntries As New List(Of (Name As String, Data As Byte()))()

        If hasOverlays Then
            overlaysJsonBytes = Encoding.UTF8.GetBytes(File.ReadAllText(overlaysJsonPath))
            If Directory.Exists(overlaysDir) Then
                For Each f In Directory.GetFiles(overlaysDir, "*.png").OrderBy(Function(p) p)
                    iconEntries.Add((Path.GetFileNameWithoutExtension(f), File.ReadAllBytes(f)))
                Next
            End If
            progress?.Invoke($"Overlays found: {overlaysJsonBytes.Length} Bytes JSON, {iconEntries.Count} Icons")
        End If

        Dim tileSize As Integer
        Dim probeInfo = SixLabors.ImageSharp.Image.Identify(probeTilePath)
        If probeInfo.Width <> probeInfo.Height Then
            Throw New InvalidOperationException($"Tile is not square: {probeTilePath}")
        End If
        tileSize = probeInfo.Width

        Dim compressionMode As Byte = COMPRESSION_MODE_PNG_GZIP
        Dim rawLenFixed = tileSize * tileSize * BYTES_PER_PIXEL
        Dim jsonOffset As Long = HEADER_SIZE
        Dim dataOffset As Long = jsonOffset + 4 + jsonBytes.Length

        Dim tmpPath = outputCmdPath & ".tmp"
        Using fs As New FileStream(tmpPath, FileMode.Create, FileAccess.ReadWrite,
                                   FileShare.None, bufferSize:=1 << 20,
                                   options:=FileOptions.SequentialScan)
            Using bw As New BinaryWriter(fs, Encoding.UTF8, leaveOpen:=True)

                bw.Write(Encoding.ASCII.GetBytes(MAGIC))
                bw.Write(VERSION)
                bw.Write(CInt(totalCount))
                bw.Write(jsonOffset)
                Dim indexOffsetFieldPos = fs.Position
                bw.Write(CLng(0))
                bw.Write(dataOffset)
                bw.Write(CShort(ENTRY_SIZE))
                bw.Write(CInt(tileSize))
                bw.Write(compressionMode)
                bw.Write(CByte(gzipLevel))
                Dim overlaysOffsetFieldPos = fs.Position
                bw.Write(CLng(0))
                bw.Write(New Byte(HEADER_SIZE - 49 - 1) {})

                If fs.Position <> jsonOffset Then
                    Throw New InvalidOperationException($"Header-Error: Position {fs.Position} ≠ jsonOffset {jsonOffset}")
                End If

                bw.Write(CInt(jsonBytes.Length))
                bw.Write(jsonBytes)

                If fs.Position <> dataOffset Then
                    Throw New InvalidOperationException($"JSON-Error: Position {fs.Position} ≠ dataOffset {dataOffset}")
                End If

                Dim boundedCapacity = encodeDegreeOfParallelism * 4
                Using queue As New BlockingCollection(Of PackResult)(boundedCapacity)

                    Dim decodeSw = Stopwatch.StartNew()
                    Dim doneCount = 0

                    Dim producerTask = Task.Run(
                        Sub()
                            Try
                                Dim encodeParallelOptions As New ParallelOptions With {
                                    .MaxDegreeOfParallelism = encodeDegreeOfParallelism
                                }
                                Parallel.ForEach(xDirEntries, encodeParallelOptions,
                                    Sub(item)
                                        Dim xVal As Integer
                                        If Not Integer.TryParse(Path.GetFileName(item.XDir), xVal) Then Return

                                        For Each pngFile In Directory.EnumerateFiles(item.XDir, "*.png")
                                            Dim nameNoExt = Path.GetFileNameWithoutExtension(pngFile)
                                            Dim yVal As Integer
                                            If Not Integer.TryParse(nameNoExt, yVal) Then Continue For


                                            Dim pngBytes As Byte()
                                            Using pngFs As New FileStream(pngFile, FileMode.Open, FileAccess.Read,
                                                                           FileShare.Read, bufferSize:=1 << 16,
                                                                           options:=FileOptions.SequentialScan)
                                                pngBytes = New Byte(CInt(pngFs.Length) - 1) {}
                                                Dim readTotal = 0
                                                While readTotal < pngBytes.Length
                                                    Dim n = pngFs.Read(pngBytes, readTotal, pngBytes.Length - readTotal)
                                                    If n = 0 Then Exit While
                                                    readTotal += n
                                                End While
                                            End Using

                                            Dim gzipBytes As Byte()
                                            Using ms As New MemoryStream()
                                                Using gs As New GZipStream(ms, gzipLevel, leaveOpen:=True)
                                                    gs.Write(pngBytes, 0, pngBytes.Length)
                                                End Using
                                                gzipBytes = ms.ToArray()
                                            End Using

                                            queue.Add(New PackResult With {
                                                .Z = item.ZVal, .X = xVal, .Y = yVal,
                                                .Data = gzipBytes, .RawLen = rawLenFixed
                                            })
                                        Next
                                    End Sub)
                            Finally
                                queue.CompleteAdding()
                            End Try
                        End Sub)

                    Dim runningOffset As Long = 0
                    Dim indexEntries As New List(Of IndexRecord)(totalCount)

                    For Each item In queue.GetConsumingEnumerable()
                        bw.Write(item.Data)
                        indexEntries.Add(New IndexRecord With {
                            .Z = item.Z, .X = item.X, .Y = item.Y,
                            .Offset = runningOffset, .CompLen = item.Data.Length, .RawLen = item.RawLen
                        })
                        runningOffset += item.Data.Length

                        Dim done = Interlocked.Increment(doneCount)
                        If progress IsNot Nothing AndAlso (done Mod 200 = 0 OrElse done = totalCount) Then
                            Dim pct = CInt(Math.Round(done / totalCount * 90.0))
                            Dim rate = done / decodeSw.Elapsed.TotalSeconds
                            progress($"{pct} % – {done} / {totalCount} Tiles written ({rate:N0} T/s) …")
                        End If
                    Next

                    producerTask.Wait()

                    indexEntries.Sort(Function(a, b)
                                          If a.Z <> b.Z Then Return a.Z.CompareTo(b.Z)
                                          If a.X <> b.X Then Return a.X.CompareTo(b.X)
                                          Return a.Y.CompareTo(b.Y)
                                      End Function)

                    Dim indexOffset = fs.Position
                    For Each rec In indexEntries
                        bw.Write(CShort(rec.Z))
                        bw.Write(CInt(rec.X))
                        bw.Write(CInt(rec.Y))
                        bw.Write(rec.Offset)
                        bw.Write(CInt(rec.CompLen))
                        bw.Write(CInt(rec.RawLen))
                    Next

                    Dim overlaysSectionOffset As Long = 0
                    If hasOverlays Then
                        overlaysSectionOffset = fs.Position

                        bw.Write(CInt(overlaysJsonBytes.Length))
                        bw.Write(overlaysJsonBytes)

                        Dim iconRunning As Long = 0
                        Dim iconRelOffsets As New List(Of Long)()
                        For Each ie In iconEntries
                            iconRelOffsets.Add(iconRunning)
                            iconRunning += ie.Data.Length
                        Next

                        bw.Write(CInt(iconEntries.Count))
                        For i2 = 0 To iconEntries.Count - 1
                            Dim nameBytes = Encoding.UTF8.GetBytes(iconEntries(i2).Name)
                            bw.Write(CShort(nameBytes.Length))
                            bw.Write(nameBytes)
                            bw.Write(iconRelOffsets(i2))
                            bw.Write(CInt(iconEntries(i2).Data.Length))
                        Next

                        For Each ie In iconEntries
                            bw.Write(ie.Data)
                        Next

                        progress?.Invoke($"Overlays written: {iconEntries.Count} Icons")
                    End If

                    Dim endPos = fs.Position
                    fs.Seek(indexOffsetFieldPos, SeekOrigin.Begin)
                    bw.Write(indexOffset)
                    fs.Seek(overlaysOffsetFieldPos, SeekOrigin.Begin)
                    bw.Write(overlaysSectionOffset)
                    fs.Seek(endPos, SeekOrigin.Begin)

                End Using

            End Using
        End Using

        If File.Exists(outputCmdPath) Then File.Delete(outputCmdPath)
        File.Move(tmpPath, outputCmdPath)

        Dim sizeMB = New FileInfo(outputCmdPath).Length / (1024.0 * 1024.0)
        progress?.Invoke($"Finished! {outputCmdPath} – {sizeMB:F1} MB, TileSize={tileSize}, Mode=GZip(PNG)")
    End Sub

    Private Structure PackResult
        Public Z As Integer
        Public X As Integer
        Public Y As Integer
        Public Data As Byte()
        Public RawLen As Integer
    End Structure

    Private Structure IndexRecord
        Public Z As Integer
        Public X As Integer
        Public Y As Integer
        Public Offset As Long
        Public CompLen As Integer
        Public RawLen As Integer
    End Structure

End Class