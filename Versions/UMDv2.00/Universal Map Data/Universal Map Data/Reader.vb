Imports System.Buffers
Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.Drawing
Imports System.Drawing.Imaging
Imports System.IO
Imports System.IO.Compression
Imports System.IO.MemoryMappedFiles
Imports System.Runtime.CompilerServices
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json

Public NotInheritable Class Reader
    Implements IMapCompositor, IDisposable

    Public Property BackgroundColor As System.Drawing.Color = System.Drawing.Color.FromArgb(224, 218, 200)

    Private Const ENTRY_SIZE As Integer = 26
    Private Const BYTES_PER_PIXEL As Integer = 4
    Private Const HEADER_SIZE As Integer = 64

    Private Const MAX_SUPPORTED_ZOOM As Integer = 22

    Private ReadOnly _info As TileMapInfo
    Private ReadOnly _indexOff As Long
    Private ReadOnly _tileCount As Integer
    Private ReadOnly _entrySize As Integer
    Private ReadOnly _dataStart As Long
    Private ReadOnly _tileSize As Integer
    Private ReadOnly _compressionMode As Byte
    Private ReadOnly _gzipLevel As Byte
    Private ReadOnly _tileCache As TileCache
    Private ReadOnly _mmf As MemoryMappedFile
    Private ReadOnly _accessor As MemoryMappedViewAccessor
    Private _disposed As Boolean

    Private _reusableBmp As Bitmap
    Private _reusableBmpSize As Integer = -1

    Public Enum LoadStatus
        Ok
        NeedsAppUpdate
        Incompatible
        NotFound
        Corrupt
    End Enum

    Public NotInheritable Class LoadResult
        Public Property Status As LoadStatus
        Public Property Message As String
        Public Property Reader As Reader
    End Class

    Private Class TileMapInfo
        Public Property x1 As Double
        Public Property x2 As Double
        Public Property y1 As Double
        Public Property y2 As Double
        Public Property minZoom As Integer
        Public Property maxZoom As Integer
    End Class

    Public Class OverlayEntry
        Public Property X As Single
        Public Property Y As Single
        Public Property Name As String
        Public Property Type As String
        Public Property Width As Single
        Public Property Height As Single
        Public Property DlcGuard As Integer
        Public Property IsSecret As Boolean
    End Class

    Private Const OVERLAY_CELL_SIZE As Single = 4000.0F
    Private ReadOnly _overlayGrid As Dictionary(Of Long, List(Of OverlayEntry))
    Private ReadOnly _iconIndex As Dictionary(Of String, (Offset As Long, Len As Integer))
    Private ReadOnly _iconCache As New ConcurrentDictionary(Of String, Bitmap)(StringComparer.OrdinalIgnoreCase)

    Private Structure IndexEntry
        Public Offset As Long
        Public CompLen As Integer
        Public RawLen As Integer
    End Structure

    Private Class TileCache
        Private Class Entry
            Public Data As Byte()
            Public LastTouch As Long
            Public HitCounter As Integer
        End Class

        Private Const TOUCH_THROTTLE As Integer = 8

        Private ReadOnly _cap As Integer
        Private ReadOnly _map As New ConcurrentDictionary(Of Long, Entry)()
        Private _trimming As Integer

        Public Sub New(capacity As Integer)
            _cap = Math.Max(16, capacity)
        End Sub

        Public Function TryGet(key As Long) As Byte()
            Dim e As Entry = Nothing
            If _map.TryGetValue(key, e) Then
                If (Interlocked.Increment(e.HitCounter) And (TOUCH_THROTTLE - 1)) = 0 Then
                    Volatile.Write(e.LastTouch, Stopwatch.GetTimestamp())
                End If
                Return e.Data
            End If
            Return Nothing
        End Function

        Public Sub Put(key As Long, data As Byte())
            Dim e As New Entry With {.Data = data, .LastTouch = Stopwatch.GetTimestamp()}
            _map.TryAdd(key, e)
            If _map.Count > _cap Then TrimAsync()
        End Sub

        Private Sub TrimAsync()
            If Interlocked.CompareExchange(_trimming, 1, 0) <> 0 Then Return
            Task.Run(
                Sub()
                    Try
                        Dim target = CInt(_cap * 0.75)
                        If _map.Count <= target Then Return
                        Dim victims = _map.ToArray().
                            OrderBy(Function(kv) Volatile.Read(kv.Value.LastTouch)).
                            Take(_map.Count - target)
                        For Each v In victims
                            _map.TryRemove(v.Key, Nothing)
                        Next
                    Finally
                        Volatile.Write(_trimming, 0)
                    End Try
                End Sub)
        End Sub

        Public Sub Clear()
            _map.Clear()
        End Sub
    End Class

    Private Sub New(info As TileMapInfo, indexOff As Long, tileCount As Integer, entrySize As Integer,
                     dataStart As Long, tileSize As Integer, compressionMode As Byte,
                     gzipLevel As Byte,
                     mmf As MemoryMappedFile, accessor As MemoryMappedViewAccessor,
                     cacheCapacity As Integer,
                     overlays As List(Of OverlayEntry),
                     iconIndex As Dictionary(Of String, (Offset As Long, Len As Integer)))
        _info = info
        _indexOff = indexOff
        _tileCount = tileCount
        _entrySize = entrySize
        _dataStart = dataStart
        _tileSize = tileSize
        _compressionMode = compressionMode
        _gzipLevel = gzipLevel
        _mmf = mmf
        _accessor = accessor
        _tileCache = New TileCache(cacheCapacity)
        _iconIndex = iconIndex
        _overlayGrid = BuildOverlayGrid(overlays)
    End Sub

    Private Shared Function BuildOverlayGrid(list As List(Of OverlayEntry)) As Dictionary(Of Long, List(Of OverlayEntry))
        Dim grid As New Dictionary(Of Long, List(Of OverlayEntry))()
        If list Is Nothing Then Return grid
        For Each o In list
            Dim cx = CInt(Math.Floor(o.X / OVERLAY_CELL_SIZE))
            Dim cz = CInt(Math.Floor(o.Y / OVERLAY_CELL_SIZE))
            Dim key = OverlayCellKey(cx, cz)
            Dim lst As List(Of OverlayEntry) = Nothing
            If Not grid.TryGetValue(key, lst) Then
                lst = New List(Of OverlayEntry)()
                grid(key) = lst
            End If
            lst.Add(o)
        Next
        Return grid
    End Function

    Private Shared Function OverlayCellKey(cx As Integer, cz As Integer) As Long
        Return (CLng(cx) << 32) Xor (CLng(cz) And &HFFFFFFFFL)
    End Function

    Public ReadOnly Property HasOverlays As Boolean
        Get
            Return _overlayGrid IsNot Nothing AndAlso _overlayGrid.Count > 0
        End Get
    End Property

    Public ReadOnly Property OverlayCount As Integer
        Get
            If _overlayGrid Is Nothing Then Return 0
            Dim total = 0
            For Each kv In _overlayGrid
                total += kv.Value.Count
            Next
            Return total
        End Get
    End Property

    Public ReadOnly Property OverlayIconCount As Integer
        Get
            Return If(_iconIndex?.Count, 0)
        End Get
    End Property

    Public Function GetOverlaysNear(worldX As Single, worldZ As Single, radius As Single, Optional ownedDlcMask As Integer = -1, Optional includeSecret As Boolean = False) As List(Of OverlayEntry)
        Dim result As New List(Of OverlayEntry)()
        If _overlayGrid Is Nothing OrElse radius <= 0.0F Then Return result
        If Single.IsNaN(radius) OrElse Single.IsInfinity(radius) Then Return result
        If Single.IsNaN(worldX) OrElse Single.IsNaN(worldZ) Then Return result
        If Single.IsInfinity(worldX) OrElse Single.IsInfinity(worldZ) Then Return result

        Const MaxReasonableCoord As Single = 5000000.0F   ' 5.000 km
        If Math.Abs(worldX) > MaxReasonableCoord OrElse Math.Abs(worldZ) > MaxReasonableCoord Then Return result

        Const MaxReasonableRadius As Single = 200000.0F   ' 200 km 
        Dim clampedRadius = Math.Min(radius, MaxReasonableRadius)

        Dim cellRLong = CLng(Math.Ceiling(CDbl(clampedRadius) / OVERLAY_CELL_SIZE)) + 1L
        If cellRLong > 2000L Then cellRLong = 2000L
        Dim cellR = CInt(cellRLong)

        Dim ccx = CInt(Math.Floor(worldX / OVERLAY_CELL_SIZE))
        Dim ccz = CInt(Math.Floor(worldZ / OVERLAY_CELL_SIZE))
        Dim r2 = CDbl(clampedRadius) * CDbl(clampedRadius)

        For gx = ccx - cellR To ccx + cellR
            For gz = ccz - cellR To ccz + cellR
                Dim lst As List(Of OverlayEntry) = Nothing
                If _overlayGrid.TryGetValue(OverlayCellKey(gx, gz), lst) Then
                    For Each o In lst
                        If Not includeSecret AndAlso o.IsSecret Then Continue For
                        If ownedDlcMask <> -1 AndAlso o.DlcGuard <> 0 AndAlso (ownedDlcMask And o.DlcGuard) = 0 Then Continue For
                        Dim dx = CDbl(o.X) - CDbl(worldX)
                        Dim dz = CDbl(o.Y) - CDbl(worldZ)
                        If dx * dx + dz * dz <= r2 Then result.Add(o)
                    Next
                End If
            Next
        Next
        Return result
    End Function

    Public Function GetOverlayIcon(name As String) As Bitmap
        If String.IsNullOrEmpty(name) OrElse _iconIndex Is Nothing Then Return Nothing

        Dim cached As Bitmap = Nothing
        If _iconCache.TryGetValue(name, cached) Then Return cached

        Dim entry As (Offset As Long, Len As Integer)
        If Not _iconIndex.TryGetValue(name, entry) Then Return Nothing

        Dim bytes(entry.Len - 1) As Byte
        _accessor.ReadArray(entry.Offset, bytes, 0, entry.Len)

        Dim bmp As Bitmap
        Using ms As New MemoryStream(bytes)
            Using tmp As New Bitmap(ms)
                bmp = New Bitmap(tmp)
            End Using
        End Using

        Dim winner = _iconCache.GetOrAdd(name, bmp)
        If Not ReferenceEquals(winner, bmp) Then
            bmp.Dispose()
        End If
        Return winner
    End Function

    Public Shared Function TryCreate(Optional explicitPath As String = Nothing,
                                      Optional cacheCapacity As Integer = 512) As Reader
        Dim candidates As New List(Of String)()

        If Not String.IsNullOrEmpty(explicitPath) Then
            candidates.Add(explicitPath)
        End If

        Dim dir As New DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory)
        For i = 0 To 9
            For Each f In dir.GetFiles("*.UMD")
                If Not candidates.Contains(f.FullName) Then candidates.Add(f.FullName)
            Next
            If dir.Parent Is Nothing Then Exit For
            dir = dir.Parent
        Next

        For Each path In candidates
            Dim result = TryLoadWithStatus(path, cacheCapacity)
            If result.Status = LoadStatus.Ok Then Return result.Reader
        Next
        Return Nothing
    End Function

    Public Shared Function TryLoadWithStatus(UMDPath As String, Optional cacheCapacity As Integer = 512) As LoadResult
        If Not File.Exists(UMDPath) Then
            Return New LoadResult With {.Status = LoadStatus.NotFound, .Message = $"File not found: {UMDPath}"}
        End If

        Dim mmf As MemoryMappedFile = Nothing
        Dim accessor As MemoryMappedViewAccessor = Nothing
        Try
            Dim fileLen = New FileInfo(UMDPath).Length
            If fileLen < FormatVersion.MAGIC_FIELD_SIZE Then
                Return New LoadResult With {.Status = LoadStatus.Corrupt, .Message = "File is too small / corrupt."}
            End If

            mmf = MemoryMappedFile.CreateFromFile(UMDPath, FileMode.Open, Nothing, 0, MemoryMappedFileAccess.Read)
            accessor = mmf.CreateViewAccessor(0, fileLen, MemoryMappedFileAccess.Read)

            Dim magicBytes(FormatVersion.MAGIC_FIELD_SIZE - 1) As Byte
            accessor.ReadArray(0, magicBytes, 0, FormatVersion.MAGIC_FIELD_SIZE)
            Dim magicRaw = Encoding.ASCII.GetString(magicBytes).TrimEnd(Chr(0))

            Dim compat = FormatVersion.CheckCompatibility(magicRaw, Path.GetFileName(UMDPath))

            Select Case compat.Status
                Case FormatVersion.CompatibilityStatus.Incompatible
                    accessor.Dispose() : mmf.Dispose()
                    Return New LoadResult With {.Status = LoadStatus.Incompatible, .Message = compat.Message}

                Case FormatVersion.CompatibilityStatus.NeedsAppUpdate
                    accessor.Dispose() : mmf.Dispose()
                    Return New LoadResult With {.Status = LoadStatus.NeedsAppUpdate, .Message = compat.Message}
            End Select

            Dim pos As Long = FormatVersion.MAGIC_FIELD_SIZE
            Dim ver = accessor.ReadByte(pos) : pos += 1
            Dim tileCount = accessor.ReadInt32(pos) : pos += 4
            Dim jsonOff = accessor.ReadInt64(pos) : pos += 8
            Dim indexOff = accessor.ReadInt64(pos) : pos += 8
            Dim dataOff = accessor.ReadInt64(pos) : pos += 8
            Dim entrySize = accessor.ReadInt16(pos) : pos += 2
            Dim tileSize = accessor.ReadInt32(pos) : pos += 4
            Dim compMode = accessor.ReadByte(pos) : pos += 1

            Dim gzipLevel = accessor.ReadByte(pos) : pos += 1
            Dim overlaysSectionOffset = accessor.ReadInt64(pos) : pos += 8

            Dim jsonLen = accessor.ReadInt32(jsonOff)
            Dim jsonBytes(jsonLen - 1) As Byte
            accessor.ReadArray(jsonOff + 4, jsonBytes, 0, jsonLen)
            Dim info = JsonConvert.DeserializeObject(Of TileMapInfo)(Encoding.UTF8.GetString(jsonBytes))
            If info Is Nothing Then
                accessor.Dispose() : mmf.Dispose()
                Return New LoadResult With {.Status = LoadStatus.Corrupt, .Message = "TileMapInfo in JSON invalid."}
            End If

            Dim overlays As List(Of OverlayEntry) = Nothing
            Dim iconIndex As Dictionary(Of String, (Offset As Long, Len As Integer)) = Nothing

            If overlaysSectionOffset > 0 Then
                Try
                    Dim p = overlaysSectionOffset
                    Dim ovJsonLen = accessor.ReadInt32(p) : p += 4
                    Dim ovJsonBytes(ovJsonLen - 1) As Byte
                    accessor.ReadArray(p, ovJsonBytes, 0, ovJsonLen) : p += ovJsonLen
                    overlays = JsonConvert.DeserializeObject(Of List(Of OverlayEntry))(Encoding.UTF8.GetString(ovJsonBytes))

                    Dim iconCount = accessor.ReadInt32(p) : p += 4
                    Dim relIndex As New Dictionary(Of String, (Offset As Long, Len As Integer))(iconCount, StringComparer.OrdinalIgnoreCase)
                    For i = 0 To iconCount - 1
                        Dim nameLen = accessor.ReadInt16(p) : p += 2
                        Dim nameBytes(nameLen - 1) As Byte
                        accessor.ReadArray(p, nameBytes, 0, nameLen) : p += nameLen
                        Dim name = Encoding.UTF8.GetString(nameBytes)
                        Dim relOff = accessor.ReadInt64(p) : p += 8
                        Dim len = accessor.ReadInt32(p) : p += 4
                        relIndex(name) = (relOff, len)
                    Next

                    Dim iconDataStart = p
                    Dim iconIdxAbs As New Dictionary(Of String, (Offset As Long, Len As Integer))(StringComparer.OrdinalIgnoreCase)
                    For Each kv In relIndex
                        iconIdxAbs(kv.Key) = (iconDataStart + kv.Value.Offset, kv.Value.Len)
                    Next
                    iconIndex = iconIdxAbs
                Catch
                    overlays = Nothing
                    iconIndex = Nothing
                End Try
            End If

            Dim readerInstance As New Reader(info, indexOff, tileCount, entrySize,
                                              dataOff, tileSize, compMode, gzipLevel, mmf, accessor, cacheCapacity,
                                              overlays, iconIndex)

            Return New LoadResult With {.Status = LoadStatus.Ok, .Reader = readerInstance}

        Catch ex As Exception
            accessor?.Dispose()
            mmf?.Dispose()
            Return New LoadResult With {.Status = LoadStatus.Corrupt, .Message = $"Error while loading '{Path.GetFileName(UMDPath)}': {ex.Message}"}
        End Try
    End Function
    Private Shared Function TryLoad(UMDPath As String, cacheCapacity As Integer) As Reader
        Return TryLoadWithStatus(UMDPath, cacheCapacity).Reader
    End Function

    Public ReadOnly Property IsAvailable As Boolean Implements IMapCompositor.IsAvailable
        Get
            Return _info IsNot Nothing AndAlso _tileCount > 0
        End Get
    End Property

    Public ReadOnly Property GzipLevelUsed As CompressionLevel
        Get
            Return CType(_gzipLevel, CompressionLevel)
        End Get
    End Property

    Public ReadOnly Property ExportMinZoom As Integer Implements IMapCompositor.ExportMinZoom
        Get
            Return If(_info Is Nothing, 1, Math.Max(1, _info.minZoom))
        End Get
    End Property

    Public ReadOnly Property ExportMaxZoom As Integer Implements IMapCompositor.ExportMaxZoom
        Get
            If _info Is Nothing Then Return 9
            Return Math.Max(ExportMinZoom, Math.Min(MAX_SUPPORTED_ZOOM, _info.maxZoom))
        End Get
    End Property

    Public Shared Function ComputeRenderSize(clientW As Integer, clientH As Integer) As Integer
        Dim wf = CSng(clientW)
        Dim hf = CSng(clientH)
        Dim diag = CInt(Math.Ceiling(Math.Sqrt(wf * wf + hf * hf)))
        Return Math.Max(diag, 512)
    End Function

    Public Function Render(truckX As Single, truckZ As Single, zoom As Single,
                           clientW As Integer, clientH As Integer,
                           ByRef usedTileZoom As Integer) As Bitmap Implements IMapCompositor.Render
        usedTileZoom = -1
        If Not IsAvailable OrElse zoom <= 0.0001F Then Return Nothing

        Dim outSize = ComputeRenderSize(clientW, clientH)
        Dim byteCount = outSize * outSize * BYTES_PER_PIXEL
        Dim frame = ArrayPool(Of Byte).Shared.Rent(byteCount)
        Try
            If Not RenderInto(frame, outSize, truckX, truckZ, zoom, usedTileZoom) Then
                Return Nothing
            End If

            If _reusableBmp Is Nothing OrElse _reusableBmpSize <> outSize Then
                _reusableBmp?.Dispose()
                _reusableBmp = New Bitmap(outSize, outSize, PixelFormat.Format32bppArgb)
                _reusableBmpSize = outSize
            End If

            Dim rect As New System.Drawing.Rectangle(0, 0, outSize, outSize)
            Dim bmpData = _reusableBmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb)
            Try
                Dim rowBytes = outSize * BYTES_PER_PIXEL
                If bmpData.Stride = rowBytes Then
                    Marshal.Copy(frame, 0, bmpData.Scan0, rowBytes * outSize)
                Else
                    For row = 0 To outSize - 1
                        Marshal.Copy(frame, row * rowBytes, IntPtr.Add(bmpData.Scan0, row * bmpData.Stride), rowBytes)
                    Next
                End If
            Finally
                _reusableBmp.UnlockBits(bmpData)
            End Try
            Return _reusableBmp
        Finally
            ArrayPool(Of Byte).Shared.Return(frame)
        End Try
    End Function

    Public Function RenderInto(frame As Byte(), outSize As Integer,
                                truckX As Single, truckZ As Single, zoom As Single,
                                ByRef usedTileZoom As Integer) As Boolean
        usedTileZoom = -1
        If Not IsAvailable OrElse zoom <= 0.0001F Then Return False

        Dim halfWorld = (outSize / 2.0F) / zoom
        Dim wx0 = truckX - halfWorld
        Dim wx1 = truckX + halfWorld
        Dim wz0 = truckZ - halfWorld
        Dim wz1 = truckZ + halfWorld

        Dim xSpan = _info.x2 - _info.x1
        Dim ySpan = _info.y2 - _info.y1
        If xSpan <= 0 OrElse ySpan <= 0 Then Return False

        Dim mppTarget = (2.0F * halfWorld) / outSize
        Dim zUse = PickZoomFromMpp(mppTarget)
        usedTileZoom = zUse

        Dim n = 1 << zUse
        Dim tileWorldW = CSng(xSpan / n)
        Dim tileWorldH = CSng(ySpan / n)

        Dim tx0 = Clamp(CInt(Math.Floor((wx0 - _info.x1) / tileWorldW)), 0, n - 1)
        Dim tx1 = Clamp(CInt(Math.Floor((wx1 - _info.x1) / tileWorldW)), 0, n - 1)
        Dim ty0 = Clamp(CInt(Math.Floor((wz0 - _info.y1) / tileWorldH)), 0, n - 1)
        Dim ty1 = Clamp(CInt(Math.Floor((wz1 - _info.y1) / tileWorldH)), 0, n - 1)

        Dim fullyCovered = (wx0 >= _info.x1 + tx0 * tileWorldW) AndAlso
                            (wx1 <= _info.x1 + (tx1 + 1) * tileWorldW) AndAlso
                            (wz0 >= _info.y1 + ty0 * tileWorldH) AndAlso
                            (wz1 <= _info.y1 + (ty1 + 1) * tileWorldH)
        If Not fullyCovered Then FillBackground(frame, outSize)

        Dim tileCountX = tx1 - tx0 + 1
        Dim tileCountY = ty1 - ty0 + 1
        Dim totalTiles = tileCountX * tileCountY

        If totalTiles >= 6 Then
            Parallel.For(0, totalTiles,
                Sub(idx)
                    Dim tx = tx0 + idx Mod tileCountX
                    Dim ty = ty0 + idx \ tileCountX
                    BlitOneTile(frame, outSize, zUse, tx, ty, tileWorldW, tileWorldH, wx0, wx1, wz0, wz1)
                End Sub)
        Else
            For tx = tx0 To tx1
                For ty = ty0 To ty1
                    BlitOneTile(frame, outSize, zUse, tx, ty, tileWorldW, tileWorldH, wx0, wx1, wz0, wz1)
                Next
            Next
        End If

        Return True
    End Function

    Private Sub BlitOneTile(frame As Byte(), outSize As Integer, zUse As Integer, tx As Integer, ty As Integer,
                             tileWorldW As Single, tileWorldH As Single,
                             wx0 As Single, wx1 As Single, wz0 As Single, wz1 As Single)
        Dim twx0 = CSng(_info.x1 + tx * tileWorldW)
        Dim twx1 = twx0 + tileWorldW
        Dim twz0 = CSng(_info.y1 + ty * tileWorldH)
        Dim twz1 = twz0 + tileWorldH

        Dim px0 = (twx0 - wx0) / (wx1 - wx0) * outSize
        Dim px1 = (twx1 - wx0) / (wx1 - wx0) * outSize
        Dim py0 = (twz0 - wz0) / (wz1 - wz0) * outSize
        Dim py1 = (twz1 - wz0) / (wz1 - wz0) * outSize

        Dim tile = GetTileRaw(zUse, tx, ty)
        If tile Is Nothing Then
            FillBackgroundRect(frame, outSize, px0, px1, py0, py1)
            Return
        End If

        BlitTileNearest(frame, outSize, tile, px0, px1, py0, py1)
    End Sub

    Public Function BuildOfflineRenderZoomStops() As Single() Implements IMapCompositor.BuildOfflineRenderZoomStops
        If Not IsAvailable Then Return Array.Empty(Of Single)()
        Dim xSpan = CSng(_info.x2 - _info.x1)
        Dim r0 = CSng(1.35 * 256.0 / xSpan * 0.5)
        Const r1 As Single = 18.0F
        Const steps = 480

        Dim bands As New List(Of (pick As Integer, rStart As Single, rEnd As Single))()
        Dim bandStartR = r0
        Dim lastPick = PickAtRenderZoom(r0)

        For i = 1 To steps
            Dim t = i / CSng(steps)
            Dim r = r0 * CSng(Math.Pow(CDbl(r1 / r0), CDbl(t)))
            Dim p = PickAtRenderZoom(r)
            If p <> lastPick OrElse i = steps Then
                If lastPick >= ExportMinZoom AndAlso lastPick <= ExportMaxZoom Then
                    bands.Add((lastPick, bandStartR, r))
                End If
                bandStartR = r
                lastPick = p
            End If
        Next

        If bands.Count = 0 Then Return Array.Empty(Of Single)()

        Dim stepsPerBand = 6
        Dim candidates As New List(Of Single)()
        For Each b In bands
            Dim span = b.rEnd - b.rStart
            For j = 0 To stepsPerBand - 1
                Dim v = If(stepsPerBand = 1,
                           (b.rStart + b.rEnd) * 0.5F,
                           b.rStart + span * (j / CSng(stepsPerBand - 1)))
                candidates.Add(v)
            Next
        Next

        candidates = candidates.OrderBy(Function(x) x).ToList()

        Const dedupRelativeThreshold As Single = 1.02F
        Dim dedup As New List(Of Single)()
        Dim lastAdded As Single = -1.0F
        For Each v In candidates
            If dedup.Count = 0 OrElse lastAdded <= 0.0F OrElse (v / lastAdded) > dedupRelativeThreshold Then
                dedup.Add(v)
                lastAdded = v
            End If
        Next
        Return dedup.ToArray()
    End Function

    Private Function TryFindIndexEntry(z As Integer, x As Integer, y As Integer, ByRef entry As IndexEntry) As Boolean
        Dim lo As Long = 0
        Dim hi As Long = CLng(_tileCount) - 1
        While lo <= hi
            Dim mid = lo + (hi - lo) \ 2L
            Dim p = _indexOff + mid * _entrySize
            Dim mz = CInt(_accessor.ReadInt16(p))
            Dim mx = _accessor.ReadInt32(p + 2)
            Dim my = _accessor.ReadInt32(p + 6)

            Dim cmp As Integer
            If mz <> z Then
                cmp = mz.CompareTo(z)
            ElseIf mx <> x Then
                cmp = mx.CompareTo(x)
            Else
                cmp = my.CompareTo(y)
            End If

            If cmp = 0 Then
                Dim off = _accessor.ReadInt64(p + 10)
                Dim clen = _accessor.ReadInt32(p + 18)
                Dim rlen = _accessor.ReadInt32(p + 22)
                entry = New IndexEntry With {.Offset = off, .CompLen = clen, .RawLen = rlen}
                Return True
            ElseIf cmp < 0 Then
                lo = mid + 1L
            Else
                hi = mid - 1L
            End If
        End While
        entry = Nothing
        Return False
    End Function

    Private Function GetTileRaw(z As Integer, x As Integer, y As Integer) As Byte()
        Dim key = PackKey(z, x, y)

        Dim cached = _tileCache.TryGet(key)
        If cached IsNot Nothing Then Return cached

        Dim entry As IndexEntry
        If Not TryFindIndexEntry(z, x, y, entry) Then Return Nothing

        Dim compressed = ArrayPool(Of Byte).Shared.Rent(entry.CompLen)
        Try
            _accessor.ReadArray(_dataStart + entry.Offset, compressed, 0, entry.CompLen)

            Dim raw(entry.RawLen - 1) As Byte
            If _compressionMode = 0 Then
                Array.Copy(compressed, raw, entry.RawLen)
            ElseIf _compressionMode = 2 Then
                Using ms As New MemoryStream(compressed, 0, entry.CompLen, writable:=False)
                    DecodeImageToBgra32(ms, raw)
                End Using

            ElseIf _compressionMode = 3 Then
                Using cms As New MemoryStream(compressed, 0, entry.CompLen, writable:=False)
                    Using gs As New GZipStream(cms, CompressionMode.Decompress)
                        Using pngMs As New MemoryStream()
                            gs.CopyTo(pngMs)
                            pngMs.Position = 0
                            DecodeImageToBgra32(pngMs, raw)
                        End Using

                    End Using
                End Using
            Else
                Using ms As New MemoryStream(compressed, 0, entry.CompLen, writable:=False)
                    Using ds As New DeflateStream(ms, CompressionMode.Decompress)
                        Dim total = 0
                        While total < raw.Length
                            Dim r = ds.Read(raw, total, raw.Length - total)
                            If r = 0 Then Exit While
                            total += r
                        End While
                    End Using
                End Using
            End If

            _tileCache.Put(key, raw)
            Return raw
        Finally
            ArrayPool(Of Byte).Shared.Return(compressed)
        End Try
    End Function

    Private Shared Sub DecodeImageToBgra32(source As Stream, dest As Byte())
        Using bmp As New Bitmap(source)
            Dim rect As New Rectangle(0, 0, bmp.Width, bmp.Height)
            Dim expected = bmp.Width * bmp.Height * BYTES_PER_PIXEL
            If dest.Length <> expected Then
                Throw New InvalidOperationException($"Unexpected Image size: expected {dest.Length} Bytes, Image provides {expected} Bytes ({bmp.Width}x{bmp.Height}).")
            End If

            Dim bmpData = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb)
            Try
                Dim rowBytes = bmp.Width * BYTES_PER_PIXEL
                If bmpData.Stride = rowBytes Then
                    Marshal.Copy(bmpData.Scan0, dest, 0, rowBytes * bmp.Height)
                Else
                    For y = 0 To bmp.Height - 1
                        Dim rowPtr = IntPtr.Add(bmpData.Scan0, y * bmpData.Stride)
                        Marshal.Copy(rowPtr, dest, y * rowBytes, rowBytes)
                    Next
                End If
            Finally
                bmp.UnlockBits(bmpData)
            End Try
        End Using
    End Sub


    <MethodImpl(MethodImplOptions.AggressiveOptimization)>
    Private Sub BlitTileNearest(frame As Byte(), outSize As Integer, tile As Byte(), px0 As Single, px1 As Single, py0 As Single, py1 As Single)
        Dim ix0 = Math.Max(0, CInt(Math.Floor(px0)))
        Dim iy0 = Math.Max(0, CInt(Math.Floor(py0)))
        Dim ix1 = Math.Min(outSize, CInt(Math.Ceiling(px1)) + 1)
        Dim iy1 = Math.Min(outSize, CInt(Math.Ceiling(py1)) + 1)
        If ix1 <= ix0 OrElse iy1 <= iy0 Then Return

        Dim destW = px1 - px0
        Dim destH = py1 - py0
        If destW <= 0 OrElse destH <= 0 Then Return

        Dim scaleX = _tileSize / destW
        Dim scaleY = _tileSize / destH

        If Math.Abs(scaleX - 1.0F) < 0.001F AndAlso Math.Abs(scaleY - 1.0F) < 0.001F AndAlso
           Math.Abs(px0 - ix0) < 0.001F AndAlso Math.Abs(py0 - iy0) < 0.001F AndAlso
           (ix1 - ix0) >= _tileSize AndAlso (iy1 - iy0) >= _tileSize Then
            Dim rowBytes = _tileSize * BYTES_PER_PIXEL
            For sy = 0 To _tileSize - 1
                Dim dy = iy0 + sy
                Array.Copy(tile, sy * rowBytes, frame, (dy * outSize + ix0) * BYTES_PER_PIXEL, rowBytes)
            Next
            Return
        End If

        Dim frameInts = MemoryMarshal.Cast(Of Byte, Integer)(frame.AsSpan(0, outSize * outSize * BYTES_PER_PIXEL))
        Dim tileInts = MemoryMarshal.Cast(Of Byte, Integer)(tile.AsSpan())

        Dim sxArr(ix1 - ix0 - 1) As Integer
        For dx = ix0 To ix1 - 1
            Dim sx = CInt((dx + 0.5F - px0) * scaleX)
            If sx < 0 Then sx = 0
            If sx >= _tileSize Then sx = _tileSize - 1
            sxArr(dx - ix0) = sx
        Next

        For dy = iy0 To iy1 - 1
            Dim sy = CInt((dy + 0.5F - py0) * scaleY)
            If sy < 0 Then sy = 0
            If sy >= _tileSize Then sy = _tileSize - 1
            Dim srcRow = sy * _tileSize
            Dim destRow = dy * outSize

            For dx = ix0 To ix1 - 1
                frameInts(destRow + dx) = tileInts(srcRow + sxArr(dx - ix0))
            Next
        Next
    End Sub

    Private Sub FillBackground(frame As Byte(), outSize As Integer)
        Dim argb = BackgroundColor.ToArgb() ' Byterow B,G,R,A == Int32 0xAARRGGBB (little-endian)
        Dim frameInts = MemoryMarshal.Cast(Of Byte, Integer)(frame.AsSpan(0, outSize * outSize * BYTES_PER_PIXEL))
        frameInts.Fill(argb)
    End Sub

    Private Sub FillBackgroundRect(frame As Byte(), outSize As Integer, px0 As Single, px1 As Single, py0 As Single, py1 As Single)
        Dim ix0 = Clamp(CInt(Math.Floor(px0)), 0, outSize)
        Dim iy0 = Clamp(CInt(Math.Floor(py0)), 0, outSize)
        Dim ix1 = Clamp(CInt(Math.Ceiling(px1)) + 1, 0, outSize)
        Dim iy1 = Clamp(CInt(Math.Ceiling(py1)) + 1, 0, outSize)
        If ix1 <= ix0 OrElse iy1 <= iy0 Then Return

        Dim argb = BackgroundColor.ToArgb()
        Dim frameInts = MemoryMarshal.Cast(Of Byte, Integer)(frame.AsSpan(0, outSize * outSize * BYTES_PER_PIXEL))
        For dy = iy0 To iy1 - 1
            Dim destRow = dy * outSize
            frameInts.Slice(destRow + ix0, ix1 - ix0).Fill(argb)
        Next
    End Sub

    Private Shared Function Clamp(v As Integer, lo As Integer, hi As Integer) As Integer
        If v < lo Then Return lo
        If v > hi Then Return hi
        Return v
    End Function

    Private Shared Function PackKey(z As Integer, x As Integer, y As Integer) As Long
        Return (CLng(z) << 44) Or (CLng(x) << 22) Or CLng(y)
    End Function

    Private Function PickZoomFromMpp(mppTarget As Single) As Integer
        Dim lo = ExportMinZoom
        Dim hi = ExportMaxZoom
        Dim best = hi
        For zt = lo To hi
            Dim n = 1 << zt
            Dim tileWorldW = CSng((_info.x2 - _info.x1) / n)
            Dim mppTile = tileWorldW / 256.0F
            If mppTile <= mppTarget * 1.35F Then
                best = zt
                Exit For
            End If
        Next
        Return best
    End Function

    Private Function PickAtRenderZoom(renderZoom As Single) As Integer
        If renderZoom <= 0.000001F Then Return ExportMinZoom
        Return PickZoomFromMpp(1.0F / renderZoom)
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        _tileCache?.Clear()
        _reusableBmp?.Dispose()
        _reusableBmp = Nothing
        For Each kv In _iconCache
            kv.Value?.Dispose()
        Next
        _iconCache.Clear()
        _accessor?.Dispose()
        _mmf?.Dispose()
    End Sub

    Public Shared Function TryLoadForVerification(path As String) As Reader
        Return TryLoad(path, cacheCapacity:=16)
    End Function

End Class