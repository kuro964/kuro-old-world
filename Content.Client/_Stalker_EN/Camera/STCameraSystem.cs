using System.IO;
using Content.Client.DoAfter;
using Content.Client.Viewport;
using Content.Shared._Stalker_EN.Camera;
using Robust.Client.Graphics;
using Robust.Client.State;
using Robust.Client.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Content.Client._Stalker_EN.Camera;

/// <summary>
/// Client-side camera system: captures viewport, resizes, applies effects, JPEG-encodes, sends to server.
/// </summary>
public sealed class STCameraSystem : SharedSTCameraSystem
{
    [Dependency] private readonly IOverlayManager _overlayManager = default!;
    [Dependency] private readonly IStateManager _stateManager = default!;

    private const int PhotoWidth = 480;
    private const int PhotoHeight = 360;

    // BT.601 luminance coefficients
    private const float LumaR = 0.299f;
    private const float LumaG = 0.587f;
    private const float LumaB = 0.114f;

    // Polaroid effect tuning
    private const float PolaroidContrast = 0.85f;
    private const float PolaroidSaturation = 0.8f;
    private const float PolaroidShadowLift = 20f / 255f;
    private const float PolaroidWarmR = 10f / 255f;
    private const float PolaroidWarmB = 15f / 255f;
    private const float VignetteStart = 0.7f;
    private const float VignetteStrength = 0.25f;

    // Glitch effect probabilities and ranges
    private const double GlitchScanlineChance = 0.08;
    private const int GlitchScanlineMinOffset = 5;
    private const int GlitchScanlineMaxOffset = 31;
    private const double GlitchChannelSplitChance = 0.05;
    private const int GlitchChannelMinShift = 2;
    private const int GlitchChannelMaxShift = 7;
    private const double GlitchNoiseChance = 0.005;

    /// <inheritdoc />
    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<STCaptureViewportRequestEvent>(OnCaptureViewportRequestEvent);
    }

    private void OnCaptureViewportRequestEvent(STCaptureViewportRequestEvent ev)
    {
        if (_stateManager.CurrentState is not IMainViewportState state)
            return;

        var token = ev.Token;
        var effect = ev.Effect;

        // Temporarily hide the DoAfter overlay so the progress bar doesn't appear in the photo.
        _overlayManager.TryGetOverlay<DoAfterOverlay>(out var doAfterOverlay);
        if (doAfterOverlay != null)
            _overlayManager.RemoveOverlay(doAfterOverlay);

        _overlayManager.TryGetOverlay<Content.Client._Stalker_EN.NPC.Awareness.STNoticeMeterOverlay>(out var noticeOverlay);
        if (noticeOverlay != null)
            _overlayManager.RemoveOverlay(noticeOverlay);

        state.Viewport.Viewport.Screenshot(screenshot =>
        {
            if (doAfterOverlay != null)
                _overlayManager.AddOverlay(doAfterOverlay);

            if (noticeOverlay != null)
                _overlayManager.AddOverlay(noticeOverlay);

            ProcessScreenshot(screenshot, token, effect);
        });
    }

    private void ProcessScreenshot(Image<Rgba32> screenshot, Guid token, STPhotoEffect effect)
    {
        // Clone immediately -- the original image is shared with other screenshot callbacks.
        // Do NOT mutate or dispose the original.
        using var resized = screenshot.Clone(ctx => ctx.Resize(PhotoWidth, PhotoHeight));

        // Apply effect via direct pixel manipulation (sandbox-safe).
        ApplyEffect(resized, effect, token);

        using var stream = new MemoryStream();
        resized.SaveAsJpeg(stream);
        var imageData = stream.ToArray();

        RaiseNetworkEvent(new STCaptureViewportResponseEvent
        {
            Token = token,
            ImageData = imageData,
        });
    }

    /// <summary>
    /// Applies a photo effect using sandbox-safe pixel manipulation via BMP serialization.
    /// </summary>
    private static void ApplyEffect(Image<Rgba32> image, STPhotoEffect effect, Guid token)
    {
        switch (effect)
        {
            case STPhotoEffect.None:
                break;
            case STPhotoEffect.Polaroid:
                ApplyPolaroid(image);
                break;
            case STPhotoEffect.Glitch:
                ApplyGlitch(image, token);
                break;
        }
    }

    /// <summary>
    /// Warm, faded Polaroid aesthetic: compressed contrast, lifted shadows,
    /// warm color shift, slight desaturation, and gentle vignette.
    /// </summary>
    private static void ApplyPolaroid(Image<Rgba32> image)
    {
        var width = image.Width;
        var height = image.Height;
        var cx = width / 2f;
        var cy = height / 2f;
        var maxDist = MathF.Sqrt(cx * cx + cy * cy);

        var pixels = ExtractPixels(image);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var p = pixels[i];

                // Normalize to 0–1
                var rf = p.R / 255f;
                var gf = p.G / 255f;
                var bf = p.B / 255f;

                // 1. Compress contrast toward midpoint (faded look)
                rf = (rf - 0.5f) * PolaroidContrast + 0.5f;
                gf = (gf - 0.5f) * PolaroidContrast + 0.5f;
                bf = (bf - 0.5f) * PolaroidContrast + 0.5f;

                // 2. Lift shadows: add brightness inversely proportional to luminance
                var lum = LumaR * rf + LumaG * gf + LumaB * bf;
                var lift = (1f - Math.Clamp(lum, 0f, 1f)) * PolaroidShadowLift;
                rf += lift;
                gf += lift;
                bf += lift;

                // 3. Warm color shift
                rf += PolaroidWarmR;
                bf -= PolaroidWarmB;

                // 4. Desaturate
                var gray = LumaR * rf + LumaG * gf + LumaB * bf;
                rf = gray + PolaroidSaturation * (rf - gray);
                gf = gray + PolaroidSaturation * (gf - gray);
                bf = gray + PolaroidSaturation * (bf - gray);

                // 5. Gentle vignette: linear falloff from VignetteStart distance
                var dx = x - cx;
                var dy = y - cy;
                var dist = MathF.Sqrt(dx * dx + dy * dy) / maxDist;
                var vignette = 1f;
                if (dist > VignetteStart)
                {
                    var t = (dist - VignetteStart) / (1f - VignetteStart);
                    vignette = 1f - t * VignetteStrength;
                }

                rf *= vignette;
                gf *= vignette;
                bf *= vignette;

                var r = (byte)Math.Clamp((int)(rf * 255f), 0, 255);
                var g = (byte)Math.Clamp((int)(gf * 255f), 0, 255);
                var b = (byte)Math.Clamp((int)(bf * 255f), 0, 255);

                pixels[i] = new Rgba32(r, g, b, p.A);
            }
        }

        WritePixels(image, pixels);
    }

    /// <summary>
    /// Broken camera: B&W conversion with digital glitch artifacts —
    /// scanline displacement, RGB channel separation, and random noise.
    /// Uses deterministic RNG seeded from the photo token for consistent results.
    /// </summary>
    private static void ApplyGlitch(Image<Rgba32> image, Guid token)
    {
        var width = image.Width;
        var height = image.Height;
        var pixels = ExtractPixels(image);
        var rng = new Random(token.GetHashCode());

        // Step 1: Convert to grayscale (BT.601 luminance)
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            var gray = (byte)(LumaR * p.R + LumaG * p.G + LumaB * p.B);
            pixels[i] = new Rgba32(gray, gray, gray, p.A);
        }

        // Reusable row buffers — hoisted to avoid per-row allocations
        var rowBuf = new Rgba32[width];
        var lumBuf = new byte[width];

        // Step 2: Scanline displacement — ~8% of rows shifted horizontally
        for (var y = 0; y < height; y++)
        {
            if (rng.NextDouble() >= GlitchScanlineChance)
                continue;

            var offset = rng.Next(GlitchScanlineMinOffset, GlitchScanlineMaxOffset) * (rng.Next(2) == 0 ? 1 : -1);
            var rowStart = y * width;

            // Copy row to temp buffer, then write back shifted
            for (var x = 0; x < width; x++)
                rowBuf[x] = pixels[rowStart + x];

            for (var x = 0; x < width; x++)
            {
                var srcX = x - offset;
                pixels[rowStart + x] = srcX >= 0 && srcX < width
                    ? rowBuf[srcX]
                    : new Rgba32(0, 0, 0, 255);
            }
        }

        // Step 3: RGB channel separation — ~5% of rows get R shifted left, B shifted right
        for (var y = 0; y < height; y++)
        {
            if (rng.NextDouble() >= GlitchChannelSplitChance)
                continue;

            var shift = rng.Next(GlitchChannelMinShift, GlitchChannelMaxShift);
            var rowStart = y * width;

            // Read current row luminance values
            for (var x = 0; x < width; x++)
                lumBuf[x] = pixels[rowStart + x].R; // grayscale, so R == G == B

            for (var x = 0; x < width; x++)
            {
                // R channel shifted left
                var rSrc = x + shift;
                var r = rSrc < width ? lumBuf[rSrc] : lumBuf[width - 1];

                // G channel stays
                var g = lumBuf[x];

                // B channel shifted right
                var bSrc = x - shift;
                var b = bSrc >= 0 ? lumBuf[bSrc] : lumBuf[0];

                pixels[rowStart + x] = new Rgba32(r, g, b, pixels[rowStart + x].A);
            }
        }

        // Step 4: Salt-and-pepper noise
        for (var i = 0; i < pixels.Length; i++)
        {
            if (rng.NextDouble() >= GlitchNoiseChance)
                continue;

            var noise = rng.Next(2) == 0 ? (byte)0 : (byte)255;
            pixels[i] = new Rgba32(noise, noise, noise, pixels[i].A);
        }

        WritePixels(image, pixels);
    }

    /// <summary>
    /// Reads all pixels from an image via BMP serialization.
    /// This avoids GetPixelSpan() which is not whitelisted in the RobustToolbox sandbox.
    /// </summary>
    private static Rgba32[] ExtractPixels(Image<Rgba32> image)
    {
        using var ms = new MemoryStream();
        image.SaveAsBmp(ms);
        var bmp = ms.ToArray();

        // BMP file header: pixel data offset at bytes 10–13
        var dataOffset = BitConverter.ToInt32(bmp, 10);
        // DIB header: width at bytes 18–21, height at bytes 22–25, bpp at bytes 28–29
        var width = BitConverter.ToInt32(bmp, 18);
        var rawHeight = BitConverter.ToInt32(bmp, 22);
        var bpp = BitConverter.ToInt16(bmp, 28);
        var height = Math.Abs(rawHeight);
        var topDown = rawHeight < 0;
        var bytesPerPixel = bpp / 8;
        var stride = (width * bytesPerPixel + 3) & ~3; // rows padded to 4-byte boundary

        var pixels = new Rgba32[width * height];
        for (var y = 0; y < height; y++)
        {
            // BMP stores rows bottom-up by default; negative height means top-down
            var srcRow = topDown ? y : height - 1 - y;
            var rowOffset = dataOffset + srcRow * stride;
            for (var x = 0; x < width; x++)
            {
                var px = rowOffset + x * bytesPerPixel;
                // BMP pixel order is BGRA
                var b = bmp[px];
                var g = bmp[px + 1];
                var r = bmp[px + 2];
                var a = bytesPerPixel >= 4 ? bmp[px + 3] : (byte) 255;
                pixels[y * width + x] = new Rgba32(r, g, b, a);
            }
        }

        return pixels;
    }

    /// <summary>
    /// Writes a pixel array back to an image using the whitelisted indexer (set_Item).
    /// </summary>
    private static void WritePixels(Image<Rgba32> image, Rgba32[] pixels)
    {
        var width = image.Width;
        var height = image.Height;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = pixels[y * width + x];
            }
        }
    }
}
