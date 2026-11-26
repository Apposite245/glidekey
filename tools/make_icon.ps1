# Generates cs\assets\glidekey.ico — the app icon used by the exe (taskbar, pinned shortcuts,
# Alt-Tab), the window and the tray. Each size is drawn at 4x and downscaled, so the small
# sizes stay crisp; tiny sizes drop detail that would turn to mush.
param(
    [string]$Out = "$PSScriptRoot\..\cs\assets\glidekey.ico",
    [string]$PreviewPng  # optional: a side-by-side sheet of every size, for eyeballing
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# GDI+ downscaling leaves stray pixels at the edges (a bright dot at 0,0 on the small sizes),
# so the supersampled image is reduced with an exact box filter instead. Averaging happens in
# premultiplied space, otherwise transparent pixels bleed their colour into the result.
Add-Type -ReferencedAssemblies @([System.Drawing.Bitmap].Assembly.Location, [System.Drawing.Rectangle].Assembly.Location) -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class IconScale
{
    public static Bitmap Box(Bitmap src, int size)
    {
        int f = src.Width / size, n = f * f;
        var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        var sd = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dd = dst.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var srow = new byte[sd.Stride];
        var drow = new byte[dd.Stride];
        var acc = new long[size * 4];
        for (int y = 0; y < size; y++)
        {
            Array.Clear(acc, 0, acc.Length);
            for (int sy = 0; sy < f; sy++)
            {
                Marshal.Copy(IntPtr.Add(sd.Scan0, (y * f + sy) * sd.Stride), srow, 0, sd.Stride);
                for (int x = 0; x < size; x++)
                    for (int sx = 0; sx < f; sx++)
                    {
                        int i = (x * f + sx) * 4;
                        long a = srow[i + 3];
                        acc[x * 4 + 0] += srow[i + 0] * a;
                        acc[x * 4 + 1] += srow[i + 1] * a;
                        acc[x * 4 + 2] += srow[i + 2] * a;
                        acc[x * 4 + 3] += a;
                    }
            }
            for (int x = 0; x < size; x++)
            {
                long sa = acc[x * 4 + 3];
                drow[x * 4 + 3] = (byte)((sa + n / 2) / n);
                for (int ch = 0; ch < 3; ch++)
                    drow[x * 4 + ch] = sa > 0 ? (byte)Math.Min(255L, (acc[x * 4 + ch] + sa / 2) / sa) : (byte)0;
            }
            Marshal.Copy(drow, 0, IntPtr.Add(dd.Scan0, y * dd.Stride), dd.Stride);
        }
        src.UnlockBits(sd);
        dst.UnlockBits(dd);
        return dst;
    }
}
"@
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Draws the icon into an s-by-s bitmap. All geometry is a fraction of s.
function New-IconBitmap([int]$s) {
    $ss = 4                      # supersampling factor
    $n = $s * $ss
    $bmp = [System.Drawing.Bitmap]::new($n, $n, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'

    $bx = 0.04 * $n; $by = 0.17 * $n; $bw = 0.92 * $n; $bh = 0.66 * $n
    $body = New-RoundedPath $bx $by $bw $bh (0.17 * $bw)
    $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.RectangleF]::new($bx, $by, $bw, $bh),
        [System.Drawing.Color]::FromArgb(96, 140, 255), [System.Drawing.Color]::FromArgb(150, 110, 255), 35.0)
    $g.FillPath($grad, $body)

    $detail = $s -ge 24          # key dots only where they'd still be legible
    $tiny = $s -le 20            # at 16-20 px only the glide mark survives; detail turns to mush
    $keyBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(70, 255, 255, 255))
    if ($detail) {
        $cols = 5; $rows = 2
        $kw = $bw * 0.138; $kh = $kw
        $gapX = ($bw - $cols * $kw) / ($cols + 1)
        $top = $by + $bh * 0.16
        $gapY = $bh * 0.1
        for ($r = 0; $r -lt $rows; $r++) {
            for ($c = 0; $c -lt $cols; $c++) {
                $kx = $bx + $gapX + $c * ($kw + $gapX)
                $ky = $top + $r * ($kh + $gapY)
                $kp = New-RoundedPath $kx $ky $kw $kh ($kw * 0.3)
                $g.FillPath($keyBrush, $kp)
                $kp.Dispose()
            }
        }
    }
    # spacebar
    if (-not $tiny) {
    $sw = $bw * 0.46; $sh = $bh * 0.13
    $sx = $bx + ($bw - $sw) / 2; $sy = $by + $bh - $sh - $bh * 0.14
    $sp = New-RoundedPath $sx $sy $sw $sh ($sh / 2)
    $g.FillPath($keyBrush, $sp)
    $sp.Dispose()
    }

    # the glide trail: a swipe curve over the keys, which is what makes this not just a keyboard
    [System.Drawing.PointF[]]$pts = $tiny ? @(
        [System.Drawing.PointF]::new($bx + $bw * 0.18, $by + $bh * 0.72),
        [System.Drawing.PointF]::new($bx + $bw * 0.50, $by + $bh * 0.24),
        [System.Drawing.PointF]::new($bx + $bw * 0.82, $by + $bh * 0.70)
    ) : @(
        [System.Drawing.PointF]::new($bx + $bw * 0.17, $by + $bh * 0.60),
        [System.Drawing.PointF]::new($bx + $bw * 0.37, $by + $bh * 0.26),
        [System.Drawing.PointF]::new($bx + $bw * 0.57, $by + $bh * 0.66),
        [System.Drawing.PointF]::new($bx + $bw * 0.84, $by + $bh * 0.22)
    )
    $glowW = $bw * ($detail ? 0.14 : 0.20)
    $coreW = $bw * ($detail ? 0.065 : 0.115)
    $glow = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(70, 255, 255, 255), $glowW)
    $core = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255), $coreW)
    foreach ($pen in $glow, $core) { $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round' }
    $g.DrawCurve($glow, $pts, 0.6)
    $g.DrawCurve($core, $pts, 0.6)
    # head of the trail
    $hr = $coreW * 0.95
    $g.FillEllipse([System.Drawing.SolidBrush]::new([System.Drawing.Color]::White),
        $pts[-1].X - $hr, $pts[-1].Y - $hr, $hr * 2, $hr * 2)   # [-1]: the tiny curve has 3 points, not 4

    $g.Dispose(); $grad.Dispose(); $keyBrush.Dispose(); $glow.Dispose(); $core.Dispose(); $body.Dispose()

    $final = [IconScale]::Box($bmp, $s)
    $bmp.Dispose()
    return $final
}

# 32-bit DIB (BITMAPINFOHEADER + bottom-up BGRA + empty AND mask) — the classic ICO payload.
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]($w * $h * 4)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    $data = $bmp.LockBits([System.Drawing.Rectangle]::new(0, 0, $w, $h),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = [byte[]]::new($w * 4)
    for ($y = $h - 1; $y -ge 0; $y--) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $row.Length)
        $bw.Write($row)
    }
    $bmp.UnlockBits($data)
    $maskRow = [byte[]]::new([Math]::Floor(($w + 31) / 32) * 4)
    for ($y = 0; $y -lt $h; $y++) { $bw.Write($maskRow) }
    $bw.Flush()
    return , $ms.ToArray()   # comma: keep it a byte[], PowerShell unrolls a returned array
}

$images = @()
$bitmaps = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $bitmaps += $bmp
    if ($s -ge 256) {
        $ms = [System.IO.MemoryStream]::new()
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += , @($s, $ms.ToArray())
    }
    else { $images += , @($s, (Get-DibBytes $bmp)) }
}

$outPath = [System.IO.Path]::GetFullPath($Out)
$fs = [System.IO.File]::Create($outPath)
$w = [System.IO.BinaryWriter]::new($fs)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $bytes = $img[1]
    $w.Write([byte]($s -ge 256 ? 0 : $s)); $w.Write([byte]($s -ge 256 ? 0 : $s))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$bytes.Length); $w.Write([int]$offset)
    $offset += $bytes.Length
}
foreach ($img in $images) { $w.Write([byte[]]$img[1]) }   # cast: Write(object[]) picks the wrong overload
$w.Flush(); $fs.Close()

if ($PreviewPng) {
    $pad = 12
    $total = ($bitmaps | Measure-Object -Property Width -Sum).Sum + $pad * ($bitmaps.Count + 1)
    $sheet = [System.Drawing.Bitmap]::new([int]$total, 300)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(30, 32, 38))
    $x = $pad
    foreach ($b in $bitmaps) {
        $g.DrawImage($b, $x, [int](150 - $b.Height / 2))
        $x += $b.Width + $pad
    }
    $g.Dispose()
    $sheet.Save([System.IO.Path]::GetFullPath($PreviewPng), [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
}
foreach ($b in $bitmaps) { $b.Dispose() }
"wrote $outPath ($((Get-Item $outPath).Length) bytes, sizes: $($sizes -join ', '))"
