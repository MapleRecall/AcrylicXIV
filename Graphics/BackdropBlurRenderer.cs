using System;
using System.Runtime.InteropServices;
using System.Text;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace AcrylicXIV.Graphics;

/// <summary>
/// Blurs a composited scene render target in-place. Driven by <see cref="RenderHooks"/> on the game render
/// thread (right after the swap-chain back buffer is bound, before the native HUD is drawn into it), so the UI
/// later draws crisply on top.
///
/// This blur shows only UNDER the UI: the scene is blurred pre-UI, the UI draws over it accumulating per-pixel
/// coverage into alpha, then a compose pass re-sharpens everything the UI does not cover.
/// </summary>
internal sealed unsafe class BackdropBlurRenderer : IDisposable
{
    // Created D3D resources (owned). Device/context are borrowed from the game each frame.
    private ComPtr<ID3D11VertexShader> vertexShader;
    private ComPtr<ID3D11PixelShader> pixelShaderCopyZeroAlpha;
    private ComPtr<ID3D11PixelShader> pixelShaderAlphaViz;
    private ComPtr<ID3D11PixelShader> pixelShaderDownsample; // dual-Kawase downsample (Dalamud-style)
    private ComPtr<ID3D11PixelShader> pixelShaderUpsample;   // dual-Kawase upsample (Dalamud-style)
    private ComPtr<ID3D11PixelShader> pixelShaderGaussian;   // separable 9-tap Gaussian (direction via constant)
    private ComPtr<ID3D11PixelShader> pixelShaderCopy;       // passthrough (bilinear upscale), writes OutputAlpha
    private ComPtr<ID3D11SamplerState> sampler;
    private ComPtr<ID3D11Buffer> constantBuffer;
    private ComPtr<ID3D11BlendState> blendOpaque;
    private ComPtr<ID3D11BlendState> blendWriteAll;
    private ComPtr<ID3D11RasterizerState> rasterizer;
    private ComPtr<ID3D11DepthStencilState> depthDisabled;

    // Scratch copy of the target (source for the blur/compose sampling).
    private ComPtr<ID3D11Texture2D> scratchTexture;
    private ComPtr<ID3D11ShaderResourceView> scratchSrv;
    private uint scratchWidth;
    private uint scratchHeight;
    private DXGI_FORMAT scratchFormat;

    // Persistent copy of the pre-UI (sharp) scene, sampled at compose time to restore non-UI areas.
    private ComPtr<ID3D11Texture2D> sharpTexture;
    private ComPtr<ID3D11ShaderResourceView> sharpSrv;
    private uint sharpWidth;
    private uint sharpHeight;
    private DXGI_FORMAT sharpFormat;

    // Persistent copy of the blurred scene captured pre-UI (before the HUD draws over it), sampled at compose
    // time so we can reconstruct the UI-only contribution and remap only the *background* blur amount.
    private ComPtr<ID3D11Texture2D> blurTexture;
    private ComPtr<ID3D11ShaderResourceView> blurSrv;
    private uint blurWidth;
    private uint blurHeight;
    private DXGI_FORMAT blurFormat;

    // Validity of the pre-UI capture for this frame, and the resource it was taken from. Compose must only run
    // when a matching pre-UI capture exists for the same bound back buffer (else it would ghost a stale frame).
    private bool underUiCaptureValid;
    private nint underUiCaptureResource;

    // Blur pyramid: progressively halved render targets (1/2, 1/4, 1/8, 1/16, 1/32) used by the low-res Gaussian
    // and Kawase algorithms, plus a separable-blur temp at the working (1/4) resolution. All are render-target +
    // shader-resource so we can render into them and sample them. Downsampling + bilinear upsampling is what
    // removes the box blur's blocky grid on fine detail (text) — and it is far cheaper for large blur.
    private const int PyramidLevels = 5;
    private readonly ComPtr<ID3D11Texture2D>[] pyrTex = new ComPtr<ID3D11Texture2D>[PyramidLevels];
    private readonly ComPtr<ID3D11RenderTargetView>[] pyrRtv = new ComPtr<ID3D11RenderTargetView>[PyramidLevels];
    private readonly ComPtr<ID3D11ShaderResourceView>[] pyrSrv = new ComPtr<ID3D11ShaderResourceView>[PyramidLevels];
    private readonly uint[] pyrW = new uint[PyramidLevels];
    private readonly uint[] pyrH = new uint[PyramidLevels];
    private DXGI_FORMAT pyrFormat;

    private ComPtr<ID3D11Texture2D> gaussTempTex;   // separable Gaussian intermediate (same size as pyramid L1)
    private ComPtr<ID3D11RenderTargetView> gaussTempRtv;
    private ComPtr<ID3D11ShaderResourceView> gaussTempSrv;
    private uint gaussTempW;
    private uint gaussTempH;

    private ComPtr<ID3D11PixelShader> pixelShaderComposeUnderUi;

    private bool pipelineReady;
    private bool pipelineFailed;

    private enum PassKind { ClearAlpha, AlphaViz }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurConstants
    {
        public float TexelX;      // source pixel size (1/srcWidth)
        public float TexelY;      // source pixel size (1/srcHeight)
        public float Radius;      // reserved (kept for the 16-byte cbuffer layout; no longer read)
        public float OutputAlpha; // alpha written by passthrough/blur passes
        public float MaskStart;   // compose: UI-alpha where bg blur starts
        public float MaskEnd;     // compose: UI-alpha where bg blur is full
        public float SkipFullscreen;
        public float DirX;        // gaussian axis / kawase offset (x, in UV)
        public float DirY;        // gaussian axis / kawase offset (y, in UV)
        public float Strength;    // kawase spread (1 + strength)
        public float GrainAmount;
        public float GrainScale;
        public float GrainSoft;   // 1 = soft value-noise grain, 0 = sharp per-pixel grain
        public float TintR;
        public float TintG;
        public float TintB;
        public float TintAmount;
        public float DistortAmount;
        public float DistortScale;
        public float FullscreenTest;
        public float Brightness;
        public float Saturation;
        public float Contrast;
        public float Pad0;
    }

    /// <summary>Copy the bound target's RGB back unchanged while forcing its alpha channel to 0 (mask baseline).</summary>
    public nint ClearBoundAlpha()
        => RunSampledPass(PassKind.ClearAlpha);

    /// <summary>Overwrite the bound target with its own alpha channel as greyscale (UI-coverage diagnostic).</summary>
    public nint VisualizeBoundAlpha()
        => RunSampledPass(PassKind.AlphaViz);

    /// <summary>Flood the currently-bound target magenta (DebugClear). Uses the *bound* RTV — not
    /// SwapChainBackBuffer, which alternates frame to frame and would flicker.</summary>
    public nint DebugClearBound()
    {
        var deviceStruct = Device.Instance();
        if (deviceStruct == null)
            return 0;
        var context = (ID3D11DeviceContext*)deviceStruct->D3D11DeviceContext;
        if (context == null)
            return 0;

        ID3D11RenderTargetView* rtv = null;
        context->OMGetRenderTargets(1, &rtv, null);
        if (rtv == null)
            return 0;
        try
        {
            var fill = stackalloc float[4] { 0.5f, 0.75f, 0.5f, 1f };
            context->ClearRenderTargetView(rtv, fill);
            return 1;
        }
        finally
        {
            rtv->Release();
        }
    }

    /// <summary>
    /// Run one full-screen pass that samples a snapshot of the currently-bound target and writes back to it.
    /// The shader/blend are chosen AFTER the pipeline is initialized (so the first-ever call can't bind a null
    /// shader). Backs the two mask-diagnostic passes (alpha-clear and coverage preview).
    /// </summary>
    private nint RunSampledPass(PassKind kind)
    {
        var deviceStruct = Device.Instance();
        if (deviceStruct == null)
            return 0;

        var device = (ID3D11Device*)deviceStruct->D3D11Forwarder;
        var context = (ID3D11DeviceContext*)deviceStruct->D3D11DeviceContext;
        if (device == null || context == null)
            return 0;

        if (pipelineFailed)
            return 0;
        if (!pipelineReady && !InitializePipeline(device))
            return 0;

        ID3D11PixelShader* ps;
        ID3D11BlendState* blend;
        switch (kind)
        {
            case PassKind.ClearAlpha: ps = pixelShaderCopyZeroAlpha.Get(); blend = blendWriteAll.Get(); break;
            default:                  ps = pixelShaderAlphaViz.Get();       blend = blendOpaque.Get();   break;
        }

        ID3D11RenderTargetView* rtv = null;
        context->OMGetRenderTargets(1, &rtv, null);
        if (rtv == null)
            return 0;

        ID3D11Resource* res = null;
        try
        {
            rtv->GetResource(&res);
            if (res == null)
                return 0;

            // Safe cast: D3D11 uses single-inheritance COM (one vtable), and this resource is a Texture2D.
            var tex2d = (ID3D11Texture2D*)res;
            if (!EnsureScratch(device, tex2d))
                return 0;

            // Snapshot the target, then run the pass sampling that snapshot back onto the live target.
            context->CopyResource((ID3D11Resource*)scratchTexture.Get(), res);
            var c = PassConstants(scratchWidth, scratchHeight, 1f);
            CaptureAndRender(context, rtv, scratchWidth, scratchHeight, ps, blend,
                scratchSrv.Get(), null, null, c);

            return (nint)res;
        }
        finally
        {
            if (res != null)
                res->Release();
            rtv->Release();
        }
    }

    /// <summary>
    /// Pre-UI (mask mode): stash the sharp scene AND the blurred scene, blur the bound target in place, and zero
    /// its alpha channel so the native UI drawn next accumulates per-pixel coverage into alpha (which
    /// <see cref="ComposeUnderUi"/> reads back). Records the captured resource so compose can verify it matches.
    /// </summary>
    public nint BeginUnderUi(Configuration config)
    {
        underUiCaptureValid = false;

        var deviceStruct = Device.Instance();
        if (deviceStruct == null)
            return 0;

        var device = (ID3D11Device*)deviceStruct->D3D11Forwarder;
        var context = (ID3D11DeviceContext*)deviceStruct->D3D11DeviceContext;
        if (device == null || context == null)
            return 0;

        if (pipelineFailed)
            return 0;
        if (!pipelineReady && !InitializePipeline(device))
            return 0;

        ID3D11RenderTargetView* rtv = null;
        context->OMGetRenderTargets(1, &rtv, null);
        if (rtv == null)
            return 0;

        ID3D11Resource* res = null;
        try
        {
            rtv->GetResource(&res);
            if (res == null)
                return 0;

            var tex2d = (ID3D11Texture2D*)res;
            if (!EnsureScratch(device, tex2d) || !EnsureSharp(device, tex2d) || !EnsureBlur(device, tex2d))
                return 0;

            // Keep a sharp copy for the compose stage.
            context->CopyResource((ID3D11Resource*)sharpTexture.Get(), res);

            // Produce the blurred background onto the bound target (alpha forced to 0 so the UI accumulates
            // coverage). Downsamples to a pyramid then blurs (smooth, no grid, cheap). Downsample=0 is a
            // pass-through (no blur), handled inside BlurPyramid.
            if (!BlurPyramid(device, context, rtv, tex2d, config))
                return 0;

            // Keep the blurred scene (before the UI draws over it) for the compose reconstruction.
            context->CopyResource((ID3D11Resource*)blurTexture.Get(), res);

            underUiCaptureResource = (nint)res;
            underUiCaptureValid = true;
            return (nint)res;
        }
        finally
        {
            if (res != null)
                res->Release();
            rtv->Release();
        }
    }

    /// <summary>
    /// Post-UI (mask mode): the bound target now holds "UI over blurred scene" in RGB and UI coverage in alpha.
    /// Reconstruct the UI-only contribution and re-blend the BACKGROUND between sharp and blurred by a remapped
    /// coverage (so the UI itself always stays intact and only the background blur amount follows the UI alpha,
    /// with configurable start/end thresholds). Skips if the pre-UI capture is missing or for a different buffer.
    /// </summary>
    public nint ComposeUnderUi(Configuration config)
    {
        if (!underUiCaptureValid)
            return 0;

        // Consume the capture up-front: whatever happens below (including an early return before the finally is
        // reached), this capture must not be reused next frame — that would ghost a stale frame.
        underUiCaptureValid = false;

        var deviceStruct = Device.Instance();
        if (deviceStruct == null)
            return 0;

        var device = (ID3D11Device*)deviceStruct->D3D11Forwarder;
        var context = (ID3D11DeviceContext*)deviceStruct->D3D11DeviceContext;
        if (device == null || context == null)
            return 0;

        if (pipelineFailed || !pipelineReady)
            return 0;
        if (sharpTexture.Get() == null || blurTexture.Get() == null)
            return 0;

        ID3D11RenderTargetView* rtv = null;
        context->OMGetRenderTargets(1, &rtv, null);
        if (rtv == null)
            return 0;

        ID3D11Resource* res = null;
        try
        {
            rtv->GetResource(&res);
            if (res == null)
                return 0;

            // Only compose if this is the SAME back buffer we captured pre-UI; otherwise we'd blend mismatched
            // frames and ghost. Consume the capture either way so a stale one can't be reused next frame.
            if ((nint)res != underUiCaptureResource)
                return 0;

            var tex2d = (ID3D11Texture2D*)res;
            if (!EnsureScratch(device, tex2d))
                return 0;

            // Snapshot the composited (UI + blurred) frame; sharp and blurred scenes are already stashed.
            context->CopyResource((ID3D11Resource*)scratchTexture.Get(), res);
            CaptureAndRender(context, rtv, scratchWidth, scratchHeight, pixelShaderComposeUnderUi.Get(),
                blendOpaque.Get(), scratchSrv.Get(), sharpSrv.Get(), blurSrv.Get(), ComposeConstants(config, scratchWidth, scratchHeight));

            return (nint)res;
        }
        finally
        {
            if (res != null)
                res->Release();
            rtv->Release();
        }
    }

    /// <summary>Drop any stashed pre-UI capture so a later compose can't reuse it (call when not in mask mode).</summary>
    public void InvalidateUnderUiCapture() => underUiCaptureValid = false;

    private void CaptureAndRender(ID3D11DeviceContext* context, ID3D11RenderTargetView* rtv,
        uint targetWidth, uint targetHeight, ID3D11PixelShader* ps, ID3D11BlendState* blend,
        ID3D11ShaderResourceView* srv0, ID3D11ShaderResourceView* srv1, ID3D11ShaderResourceView* srv2,
        in BlurConstants constants)
    {
        // --- Capture the pipeline state we are about to clobber (each Get* AddRefs; released after restore). ---
        var oldRtvs = stackalloc ID3D11RenderTargetView*[8];
        ID3D11DepthStencilView* oldDsv = null;
        context->OMGetRenderTargets(8, oldRtvs, &oldDsv);

        var oldViewports = stackalloc D3D11_VIEWPORT[16];
        uint oldViewportCount = 16;
        context->RSGetViewports(&oldViewportCount, oldViewports);

        ID3D11RasterizerState* oldRaster = null;
        context->RSGetState(&oldRaster);

        ID3D11BlendState* oldBlend = null;
        var oldBlendFactor = stackalloc float[4];
        uint oldSampleMask = 0;
        context->OMGetBlendState(&oldBlend, oldBlendFactor, &oldSampleMask);

        ID3D11DepthStencilState* oldDepth = null;
        uint oldStencilRef = 0;
        context->OMGetDepthStencilState(&oldDepth, &oldStencilRef);

        ID3D11InputLayout* oldInputLayout = null;
        context->IAGetInputLayout(&oldInputLayout);

        D3D_PRIMITIVE_TOPOLOGY oldTopology;
        context->IAGetPrimitiveTopology(&oldTopology);

        ID3D11VertexShader* oldVs = null;
        context->VSGetShader(&oldVs, null, null);

        ID3D11PixelShader* oldPs = null;
        context->PSGetShader(&oldPs, null, null);

        // A geometry shader left bound by the game would run our fullscreen-triangle VS output through it,
        // corrupting or suppressing the blur draw. Same for the tessellation (hull/domain) stages: if they are
        // left bound while we draw a non-patch topology, the draw is invalid and can be silently dropped.
        // Capture all three so we can null them for the pass and restore afterwards.
        ID3D11GeometryShader* oldGs = null;
        context->GSGetShader(&oldGs, null, null);

        ID3D11HullShader* oldHs = null;
        context->HSGetShader(&oldHs, null, null);

        ID3D11DomainShader* oldDs = null;
        context->DSGetShader(&oldDs, null, null);

        ID3D11ShaderResourceView* oldSrv0 = null;
        ID3D11ShaderResourceView* oldSrv1 = null;
        ID3D11ShaderResourceView* oldSrv2 = null;
        context->PSGetShaderResources(0, 1, &oldSrv0);
        context->PSGetShaderResources(1, 1, &oldSrv1);
        context->PSGetShaderResources(2, 1, &oldSrv2);

        ID3D11SamplerState* oldSampler = null;
        context->PSGetSamplers(0, 1, &oldSampler);

        ID3D11Buffer* oldCb = null;
        context->PSGetConstantBuffers(0, 1, &oldCb);

        // Restore scratch buffers must be allocated outside the finally block (stackalloc is disallowed there).
        var restoreSrvs = stackalloc ID3D11ShaderResourceView*[3];
        var restoreSamplers = stackalloc ID3D11SamplerState*[1];
        var restoreCbs = stackalloc ID3D11Buffer*[1];

        try
        {
            UploadConstants(context, constants);

            var rtvs = stackalloc ID3D11RenderTargetView*[1];
            rtvs[0] = rtv;
            context->OMSetRenderTargets(1, rtvs, null);

            var vp = new D3D11_VIEWPORT
            {
                TopLeftX = 0,
                TopLeftY = 0,
                Width = targetWidth,
                Height = targetHeight,
                MinDepth = 0,
                MaxDepth = 1,
            };
            context->RSSetViewports(1, &vp);
            context->RSSetState(rasterizer);

            context->OMSetBlendState(blend, null, 0xffffffff);
            context->OMSetDepthStencilState(depthDisabled, 0);

            context->IASetInputLayout(null);
            context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

            context->VSSetShader(vertexShader, null, 0);
            context->PSSetShader(ps, null, 0);
            context->GSSetShader(null, null, 0);
            context->HSSetShader(null, null, 0);
            context->DSSetShader(null, null, 0);

            var srvs = stackalloc ID3D11ShaderResourceView*[3];
            srvs[0] = srv0;
            srvs[1] = srv1;
            srvs[2] = srv2;
            context->PSSetShaderResources(0, 3, srvs);

            var samplers = stackalloc ID3D11SamplerState*[1];
            samplers[0] = sampler.Get();
            context->PSSetSamplers(0, 1, samplers);

            var cbs = stackalloc ID3D11Buffer*[1];
            cbs[0] = constantBuffer.Get();
            context->PSSetConstantBuffers(0, 1, cbs);

            context->Draw(3, 0);
        }
        finally
        {
            // --- Restore + release captured references. ---
            context->OMSetRenderTargets(8, oldRtvs, oldDsv);
            context->RSSetViewports(oldViewportCount, oldViewports);
            context->RSSetState(oldRaster);
            context->OMSetBlendState(oldBlend, oldBlendFactor, oldSampleMask);
            context->OMSetDepthStencilState(oldDepth, oldStencilRef);
            context->IASetInputLayout(oldInputLayout);
            context->IASetPrimitiveTopology(oldTopology);
            context->VSSetShader(oldVs, null, 0);
            context->PSSetShader(oldPs, null, 0);
            context->GSSetShader(oldGs, null, 0);
            context->HSSetShader(oldHs, null, 0);
            context->DSSetShader(oldDs, null, 0);

            restoreSrvs[0] = oldSrv0;
            restoreSrvs[1] = oldSrv1;
            restoreSrvs[2] = oldSrv2;
            context->PSSetShaderResources(0, 3, restoreSrvs);

            restoreSamplers[0] = oldSampler;
            context->PSSetSamplers(0, 1, restoreSamplers);

            restoreCbs[0] = oldCb;
            context->PSSetConstantBuffers(0, 1, restoreCbs);

            for (var i = 0; i < 8; i++)
                if (oldRtvs[i] != null)
                    oldRtvs[i]->Release();
            if (oldDsv != null) oldDsv->Release();
            if (oldRaster != null) oldRaster->Release();
            if (oldBlend != null) oldBlend->Release();
            if (oldDepth != null) oldDepth->Release();
            if (oldInputLayout != null) oldInputLayout->Release();
            if (oldVs != null) oldVs->Release();
            if (oldPs != null) oldPs->Release();
            if (oldGs != null) oldGs->Release();
            if (oldHs != null) oldHs->Release();
            if (oldDs != null) oldDs->Release();
            if (oldSrv0 != null) oldSrv0->Release();
            if (oldSrv1 != null) oldSrv1->Release();
            if (oldSrv2 != null) oldSrv2->Release();
            if (oldSampler != null) oldSampler->Release();
            if (oldCb != null) oldCb->Release();
        }
    }

    private void UploadConstants(ID3D11DeviceContext* context, in BlurConstants constants)
    {
        fixed (BlurConstants* p = &constants)
            context->UpdateSubresource((ID3D11Resource*)constantBuffer.Get(), 0, null, p, 0, 0);
    }

    /// <summary>Constants for the final compose pass (mask thresholds, full-screen skip, glass material).</summary>
    private BlurConstants ComposeConstants(Configuration config, uint width, uint height)
    {
        var start = Math.Clamp(Math.Min(config.MaskAlphaStart, config.MaskAlphaEnd), 0f, 1f);
        var end = Math.Clamp(Math.Max(config.MaskAlphaStart, config.MaskAlphaEnd), 0f, 1f);
        return new BlurConstants
        {
            TexelX = width > 0 ? 1f / width : 0f,
            TexelY = height > 0 ? 1f / height : 0f,
            OutputAlpha = 1f,
            MaskStart = start,
            MaskEnd = end,
            SkipFullscreen = config.SkipFullscreenUi ? 1f : 0f,
            GrainAmount = config.GrainEnabled ? Math.Clamp(config.GrainAmount, 0f, 1f) : 0f,
            GrainScale = Math.Max(config.GrainScale, 1f),
            GrainSoft = config.GrainSoft ? 1f : 0f,
            TintR = config.TintColor.X,
            TintG = config.TintColor.Y,
            TintB = config.TintColor.Z,
            TintAmount = config.TintEnabled ? Math.Clamp(config.TintAmount, 0f, 1f) : 0f,
            DistortAmount = config.DistortEnabled ? Math.Max(config.DistortAmount, 0f) : 0f,
            DistortScale = Math.Max(config.DistortScale, 0.1f),
            FullscreenTest = config.FullscreenTest ? 1f : 0f,
            Brightness = config.AdjustEnabled ? config.Brightness : 1f,
            Saturation = config.AdjustEnabled ? config.Saturation : 1f,
            Contrast = config.AdjustEnabled ? config.Contrast : 1f,
        };
    }

    /// <summary>Constants for a pass sampling a source of the given pixel size, writing the given alpha.</summary>
    private static BlurConstants PassConstants(uint srcWidth, uint srcHeight, float outputAlpha)
        => new()
        {
            TexelX = srcWidth > 0 ? 1f / srcWidth : 0f,
            TexelY = srcHeight > 0 ? 1f / srcHeight : 0f,
            OutputAlpha = outputAlpha,
        };

    private bool EnsureScratch(ID3D11Device* device, ID3D11Texture2D* target)
    {
        D3D11_TEXTURE2D_DESC desc;
        target->GetDesc(&desc);

        if (scratchTexture.Get() != null &&
            desc.Width == scratchWidth &&
            desc.Height == scratchHeight &&
            desc.Format == scratchFormat)
        {
            return true;
        }

        scratchSrv.Dispose();
        scratchTexture.Dispose();

        var scratchDesc = desc;
        scratchDesc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        scratchDesc.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        scratchDesc.CPUAccessFlags = 0;
        scratchDesc.MiscFlags = 0;

        using var tex = default(ComPtr<ID3D11Texture2D>);
        if (device->CreateTexture2D(&scratchDesc, null, tex.GetAddressOf()).FAILED)
            return false;

        using var srv = default(ComPtr<ID3D11ShaderResourceView>);
        if (device->CreateShaderResourceView((ID3D11Resource*)tex.Get(), null, srv.GetAddressOf()).FAILED)
            return false;

        tex.Swap(ref scratchTexture);
        srv.Swap(ref scratchSrv);
        scratchWidth = desc.Width;
        scratchHeight = desc.Height;
        scratchFormat = desc.Format;
        return true;
    }

    private bool EnsureSharp(ID3D11Device* device, ID3D11Texture2D* target)
    {
        D3D11_TEXTURE2D_DESC desc;
        target->GetDesc(&desc);

        if (sharpTexture.Get() != null &&
            desc.Width == sharpWidth &&
            desc.Height == sharpHeight &&
            desc.Format == sharpFormat)
        {
            return true;
        }

        sharpSrv.Dispose();
        sharpTexture.Dispose();

        var sharpDesc = desc;
        sharpDesc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        sharpDesc.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        sharpDesc.CPUAccessFlags = 0;
        sharpDesc.MiscFlags = 0;

        using var tex = default(ComPtr<ID3D11Texture2D>);
        if (device->CreateTexture2D(&sharpDesc, null, tex.GetAddressOf()).FAILED)
            return false;

        using var srv = default(ComPtr<ID3D11ShaderResourceView>);
        if (device->CreateShaderResourceView((ID3D11Resource*)tex.Get(), null, srv.GetAddressOf()).FAILED)
            return false;

        tex.Swap(ref sharpTexture);
        srv.Swap(ref sharpSrv);
        sharpWidth = desc.Width;
        sharpHeight = desc.Height;
        sharpFormat = desc.Format;
        return true;
    }

    private bool EnsureBlur(ID3D11Device* device, ID3D11Texture2D* target)
    {
        D3D11_TEXTURE2D_DESC desc;
        target->GetDesc(&desc);

        if (blurTexture.Get() != null &&
            desc.Width == blurWidth &&
            desc.Height == blurHeight &&
            desc.Format == blurFormat)
        {
            return true;
        }

        blurSrv.Dispose();
        blurTexture.Dispose();

        var blurDesc = desc;
        blurDesc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        blurDesc.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        blurDesc.CPUAccessFlags = 0;
        blurDesc.MiscFlags = 0;

        using var tex = default(ComPtr<ID3D11Texture2D>);
        if (device->CreateTexture2D(&blurDesc, null, tex.GetAddressOf()).FAILED)
            return false;

        using var srv = default(ComPtr<ID3D11ShaderResourceView>);
        if (device->CreateShaderResourceView((ID3D11Resource*)tex.Get(), null, srv.GetAddressOf()).FAILED)
            return false;

        tex.Swap(ref blurTexture);
        srv.Swap(ref blurSrv);
        blurWidth = desc.Width;
        blurHeight = desc.Height;
        blurFormat = desc.Format;
        return true;
    }

    // Produce the blurred background onto the bound target via a downsample/upsample pyramid (Gaussian or
    // Kawase). Returns false on failure (caller aborts the frame's capture).
    private bool BlurPyramid(ID3D11Device* device, ID3D11DeviceContext* context,
        ID3D11RenderTargetView* boundRtv, ID3D11Texture2D* boundTex, Configuration config)
    {
        D3D11_TEXTURE2D_DESC desc;
        boundTex->GetDesc(&desc);
        uint fullW = desc.Width, fullH = desc.Height;

        // Blur disabled: pass the sharp scene through (alpha forced to 0 so the UI still accumulates coverage).
        // The material effects (grain/tint/Distortion) then apply over the un-blurred background in the compose.
        if (!config.BlurEnabled)
        {
            CaptureAndRender(context, boundRtv, fullW, fullH, pixelShaderCopy.Get(), blendWriteAll.Get(),
                sharpSrv.Get(), null, null, PassConstants(sharpWidth, sharpHeight, 0f));
            return true;
        }

        var gaussian = config.BlurAlgorithm == BlurAlgorithm.Gaussian;
        var radius = Math.Clamp(gaussian ? config.GaussianStrength : config.KawaseStrength, 0f, 8f);

        int levels;
        float strength;   // Kawase per-pass offset (down/up). 0 for Gaussian (its blur is the separable pass).
        float gaussScale; // Gaussian kernel scale. 0 for Kawase (no separable pass).
        if (gaussian)
        {
            // Gaussian: the Downsample slider is the working resolution (it softens even at 0 strength) and the
            // strength is the kernel size. Two independent axes; the down/up passes just resample (offset 0).
            levels = Math.Clamp(config.DownsampleLevels, 0, PyramidLevels);
            strength = 0f;
            gaussScale = radius * 0.25f;
        }
        else
        {
            // Kawase: a single strength controls everything (its blur IS the down/up pyramid — no separate
            // resolution knob). Map the strength smoothly to a pyramid depth + per-pass offset so there is no
            // jump when a level is added, and strength 0 = no blur (pass the sharp scene through).
            if (radius < 0.5f)
            {
                CaptureAndRender(context, boundRtv, fullW, fullH, pixelShaderCopy.Get(), blendWriteAll.Get(),
                    sharpSrv.Get(), null, null, PassConstants(sharpWidth, sharpHeight, 0f));
                return true;
            }
            var octave = radius / 2f;
            var fullOct = (int)Math.Floor(octave);
            levels = Math.Clamp(1 + fullOct, 1, PyramidLevels);
            strength = levels < PyramidLevels ? octave - fullOct : Math.Clamp(octave - (PyramidLevels - 1), 0f, 4f);
            gaussScale = 0f;
        }

        // Downsample = Full: blur at full resolution. Use a separable Gaussian (Kawase is a multi-resolution
        // kernel, so at a single full-res level the Gaussian is the natural full-res blur for both algorithms).
        if (levels == 0)
        {
            if (!EnsureRenderTexture(ref gaussTempTex, ref gaussTempRtv, ref gaussTempSrv,
                    ref gaussTempW, ref gaussTempH, device, fullW, fullH, desc.Format))
                return false;

            var ch = PassConstants(fullW, fullH, 1f);
            ch.DirX = (fullW > 0 ? 1f / fullW : 0f) * gaussScale;
            ch.DirY = 0f;
            CaptureAndRender(context, gaussTempRtv.Get(), fullW, fullH, pixelShaderGaussian.Get(),
                blendOpaque.Get(), sharpSrv.Get(), null, null, ch);

            var cv = PassConstants(fullW, fullH, 0f);
            cv.DirX = 0f;
            cv.DirY = (fullH > 0 ? 1f / fullH : 0f) * gaussScale;
            CaptureAndRender(context, boundRtv, fullW, fullH, pixelShaderGaussian.Get(),
                blendWriteAll.Get(), gaussTempSrv.Get(), null, null, cv);
            return true;
        }

        if (!EnsurePyramid(device, fullW, fullH, desc.Format, levels))
            return false;
        var bottom = levels - 1;
        if (gaussian && !EnsureRenderTexture(ref gaussTempTex, ref gaussTempRtv, ref gaussTempSrv,
                ref gaussTempW, ref gaussTempH, device, pyrW[bottom], pyrH[bottom], desc.Format))
            return false;

        // Downsample chain: sharp scene (full) -> pyr[0] (1/2) -> ... -> pyr[levels-1].
        var srcSrv = sharpSrv.Get();
        uint srcW = fullW, srcH = fullH;
        for (var i = 0; i < levels; i++)
        {
            var c = PassConstants(srcW, srcH, 1f);
            c.Strength = strength;
            CaptureAndRender(context, pyrRtv[i].Get(), pyrW[i], pyrH[i], pixelShaderDownsample.Get(),
                blendOpaque.Get(), srcSrv, null, null, c);
            srcSrv = pyrSrv[i].Get();
            srcW = pyrW[i];
            srcH = pyrH[i];
        }

        if (gaussian)
        {
            var scale = gaussScale;
            // Horizontal: pyr[bottom] -> gaussTemp.
            var ch = PassConstants(pyrW[bottom], pyrH[bottom], 1f);
            ch.DirX = (pyrW[bottom] > 0 ? 1f / pyrW[bottom] : 0f) * scale;
            ch.DirY = 0f;
            CaptureAndRender(context, gaussTempRtv.Get(), gaussTempW, gaussTempH, pixelShaderGaussian.Get(),
                blendOpaque.Get(), pyrSrv[bottom].Get(), null, null, ch);
            // Vertical: gaussTemp -> pyr[bottom].
            var cv = PassConstants(gaussTempW, gaussTempH, 1f);
            cv.DirX = 0f;
            cv.DirY = (gaussTempH > 0 ? 1f / gaussTempH : 0f) * scale;
            CaptureAndRender(context, pyrRtv[bottom].Get(), pyrW[bottom], pyrH[bottom], pixelShaderGaussian.Get(),
                blendOpaque.Get(), gaussTempSrv.Get(), null, null, cv);
        }

        // Upsample chain: pyr[bottom] -> ... -> pyr[0], then pyr[0] -> bound target (alpha forced to 0).
        for (var i = bottom; i >= 1; i--)
        {
            var c = PassConstants(pyrW[i], pyrH[i], 1f);
            c.Strength = strength;
            CaptureAndRender(context, pyrRtv[i - 1].Get(), pyrW[i - 1], pyrH[i - 1], pixelShaderUpsample.Get(),
                blendOpaque.Get(), pyrSrv[i].Get(), null, null, c);
        }

        var cf = PassConstants(pyrW[0], pyrH[0], 0f);
        cf.Strength = strength;
        CaptureAndRender(context, boundRtv, fullW, fullH, pixelShaderUpsample.Get(), blendWriteAll.Get(),
            pyrSrv[0].Get(), null, null, cf);
        return true;
    }

    private bool EnsurePyramid(ID3D11Device* device, uint fullW, uint fullH, DXGI_FORMAT format, int levels)
    {
        if (pyrFormat != format)
        {
            for (var i = 0; i < PyramidLevels; i++)
            {
                pyrSrv[i].Dispose();
                pyrRtv[i].Dispose();
                pyrTex[i].Dispose();
                pyrW[i] = 0;
                pyrH[i] = 0;
            }
            // The Gaussian temp shares this format; invalidate it too (EnsureRenderTexture only checks size).
            gaussTempSrv.Dispose();
            gaussTempRtv.Dispose();
            gaussTempTex.Dispose();
            gaussTempW = 0;
            gaussTempH = 0;
            pyrFormat = format;
        }

        for (var i = 0; i < levels; i++)
        {
            var w = Math.Max(1u, fullW >> (i + 1));
            var h = Math.Max(1u, fullH >> (i + 1));
            if (!EnsureRenderTexture(ref pyrTex[i], ref pyrRtv[i], ref pyrSrv[i], ref pyrW[i], ref pyrH[i],
                    device, w, h, format))
                return false;
        }
        return true;
    }

    private bool EnsureRenderTexture(ref ComPtr<ID3D11Texture2D> tex, ref ComPtr<ID3D11RenderTargetView> rtv,
        ref ComPtr<ID3D11ShaderResourceView> srv, ref uint w, ref uint h,
        ID3D11Device* device, uint width, uint height, DXGI_FORMAT format)
    {
        if (tex.Get() != null && w == width && h == height)
            return true;

        srv.Dispose();
        rtv.Dispose();
        tex.Dispose();

        var d = new D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        using var t = default(ComPtr<ID3D11Texture2D>);
        if (device->CreateTexture2D(&d, null, t.GetAddressOf()).FAILED)
            return false;
        using var r = default(ComPtr<ID3D11RenderTargetView>);
        if (device->CreateRenderTargetView((ID3D11Resource*)t.Get(), null, r.GetAddressOf()).FAILED)
            return false;
        using var s = default(ComPtr<ID3D11ShaderResourceView>);
        if (device->CreateShaderResourceView((ID3D11Resource*)t.Get(), null, s.GetAddressOf()).FAILED)
            return false;

        t.Swap(ref tex);
        r.Swap(ref rtv);
        s.Swap(ref srv);
        w = width;
        h = height;
        return true;
    }

    private bool InitializePipeline(ID3D11Device* device)
    {
        try
        {
            using (var vsBlob = CompileShader(ShaderSource, "VsMain\0"u8, "vs_5_0\0"u8))
            using (var psZeroAlpha = CompileShader(ShaderSource, "PsCopyZeroAlpha\0"u8, "ps_5_0\0"u8))
            using (var psAlphaViz = CompileShader(ShaderSource, "PsAlphaViz\0"u8, "ps_5_0\0"u8))
            using (var psCompose = CompileShader(ShaderSource, "PsComposeUnderUi\0"u8, "ps_5_0\0"u8))
            using (var psDown = CompileShader(ShaderSource, "PsDownsample\0"u8, "ps_5_0\0"u8))
            using (var psUp = CompileShader(ShaderSource, "PsUpsample\0"u8, "ps_5_0\0"u8))
            using (var psGauss = CompileShader(ShaderSource, "PsGaussian\0"u8, "ps_5_0\0"u8))
            using (var psCopy = CompileShader(ShaderSource, "PsCopy\0"u8, "ps_5_0\0"u8))
            {
                fixed (ID3D11VertexShader** pp = &vertexShader.GetPinnableReference())
                    Check(device->CreateVertexShader(vsBlob.Get()->GetBufferPointer(), vsBlob.Get()->GetBufferSize(), null, pp), "CreateVertexShader");

                fixed (ID3D11PixelShader** pp = &pixelShaderCopyZeroAlpha.GetPinnableReference())
                    Check(device->CreatePixelShader(psZeroAlpha.Get()->GetBufferPointer(), psZeroAlpha.Get()->GetBufferSize(), null, pp), "CreatePixelShader(CopyZeroAlpha)");

                fixed (ID3D11PixelShader** pp = &pixelShaderAlphaViz.GetPinnableReference())
                    Check(device->CreatePixelShader(psAlphaViz.Get()->GetBufferPointer(), psAlphaViz.Get()->GetBufferSize(), null, pp), "CreatePixelShader(AlphaViz)");

                fixed (ID3D11PixelShader** pp = &pixelShaderComposeUnderUi.GetPinnableReference())
                    Check(device->CreatePixelShader(psCompose.Get()->GetBufferPointer(), psCompose.Get()->GetBufferSize(), null, pp), "CreatePixelShader(ComposeUnderUi)");

                fixed (ID3D11PixelShader** pp = &pixelShaderDownsample.GetPinnableReference())
                    Check(device->CreatePixelShader(psDown.Get()->GetBufferPointer(), psDown.Get()->GetBufferSize(), null, pp), "CreatePixelShader(Downsample)");

                fixed (ID3D11PixelShader** pp = &pixelShaderUpsample.GetPinnableReference())
                    Check(device->CreatePixelShader(psUp.Get()->GetBufferPointer(), psUp.Get()->GetBufferSize(), null, pp), "CreatePixelShader(Upsample)");

                fixed (ID3D11PixelShader** pp = &pixelShaderGaussian.GetPinnableReference())
                    Check(device->CreatePixelShader(psGauss.Get()->GetBufferPointer(), psGauss.Get()->GetBufferSize(), null, pp), "CreatePixelShader(Gaussian)");

                fixed (ID3D11PixelShader** pp = &pixelShaderCopy.GetPinnableReference())
                    Check(device->CreatePixelShader(psCopy.Get()->GetBufferPointer(), psCopy.Get()->GetBufferSize(), null, pp), "CreatePixelShader(Copy)");
            }

            var samplerDesc = new D3D11_SAMPLER_DESC
            {
                Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS,
                MinLOD = 0,
                MaxLOD = 0,
            };
            fixed (ID3D11SamplerState** pp = &sampler.GetPinnableReference())
                Check(device->CreateSamplerState(&samplerDesc, pp), "CreateSamplerState");

            var bufferDesc = new D3D11_BUFFER_DESC(
                (uint)sizeof(BlurConstants),
                (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
                D3D11_USAGE.D3D11_USAGE_DEFAULT);
            fixed (ID3D11Buffer** pp = &constantBuffer.GetPinnableReference())
                Check(device->CreateBuffer(&bufferDesc, null, pp), "CreateBuffer");

            var blendDesc = new D3D11_BLEND_DESC
            {
                RenderTarget =
                {
                    e0 =
                    {
                        BlendEnable = false,
                        // Write colour only; leave the destination alpha channel untouched (the game uses it).
                        RenderTargetWriteMask = (byte)(D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_RED
                            | D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_GREEN
                            | D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_BLUE),
                    },
                },
            };
            fixed (ID3D11BlendState** pp = &blendOpaque.GetPinnableReference())
                Check(device->CreateBlendState(&blendDesc, pp), "CreateBlendState");

            // Same as blendOpaque but also writes the alpha channel (used to reset the mask baseline to 0).
            var blendAllDesc = new D3D11_BLEND_DESC
            {
                RenderTarget =
                {
                    e0 =
                    {
                        BlendEnable = false,
                        RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
                    },
                },
            };
            fixed (ID3D11BlendState** pp = &blendWriteAll.GetPinnableReference())
                Check(device->CreateBlendState(&blendAllDesc, pp), "CreateBlendState(WriteAll)");

            var rasterDesc = new D3D11_RASTERIZER_DESC
            {
                FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
                CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
                ScissorEnable = false,
                DepthClipEnable = false,
            };
            fixed (ID3D11RasterizerState** pp = &rasterizer.GetPinnableReference())
                Check(device->CreateRasterizerState(&rasterDesc, pp), "CreateRasterizerState");

            var depthDesc = new D3D11_DEPTH_STENCIL_DESC
            {
                DepthEnable = false,
                StencilEnable = false,
            };
            fixed (ID3D11DepthStencilState** pp = &depthDisabled.GetPinnableReference())
                Check(device->CreateDepthStencilState(&depthDesc, pp), "CreateDepthStencilState");

            pipelineReady = true;
            Plugin.Log.Information("Backdrop blur pipeline initialized.");
            return true;
        }
        catch (Exception ex)
        {
            pipelineFailed = true;
            Plugin.Log.Error(ex, "Failed to initialize backdrop blur pipeline; disabling blur.");
            return false;
        }
    }

    private static ComPtr<ID3DBlob> CompileShader(string source, ReadOnlySpan<byte> entryPoint, ReadOnlySpan<byte> target)
    {
        var srcBytes = Encoding.ASCII.GetBytes(source);
        var code = default(ComPtr<ID3DBlob>);
        var errors = default(ComPtr<ID3DBlob>);
        try
        {
            HRESULT hr;
            fixed (byte* pSrc = srcBytes)
            fixed (byte* pEntry = entryPoint)
            fixed (byte* pTarget = target)
            {
                hr = DirectX.D3DCompile(
                    pSrc,
                    (nuint)srcBytes.Length,
                    null,
                    null,
                    null,
                    (sbyte*)pEntry,
                    (sbyte*)pTarget,
                    0,
                    0,
                    code.GetAddressOf(),
                    errors.GetAddressOf());
            }

            if (hr.FAILED)
            {
                var message = "unknown error";
                if (errors.Get() != null)
                    message = new string((sbyte*)errors.Get()->GetBufferPointer());
                throw new InvalidOperationException($"D3DCompile failed (0x{(uint)hr.Value:X8}): {message}");
            }

            return new ComPtr<ID3DBlob>(code);
        }
        finally
        {
            code.Dispose();
            errors.Dispose();
        }
    }

    private static void Check(HRESULT hr, string what)
    {
        if (hr.FAILED)
            throw new InvalidOperationException($"{what} failed: 0x{(uint)hr.Value:X8}");
    }

    public void Dispose()
    {
        for (var i = 0; i < PyramidLevels; i++)
        {
            pyrSrv[i].Dispose();
            pyrRtv[i].Dispose();
            pyrTex[i].Dispose();
        }
        gaussTempSrv.Dispose();
        gaussTempRtv.Dispose();
        gaussTempTex.Dispose();
        blurSrv.Dispose();
        blurTexture.Dispose();
        sharpSrv.Dispose();
        sharpTexture.Dispose();
        scratchSrv.Dispose();
        scratchTexture.Dispose();
        depthDisabled.Dispose();
        rasterizer.Dispose();
        blendWriteAll.Dispose();
        blendOpaque.Dispose();
        constantBuffer.Dispose();
        sampler.Dispose();
        pixelShaderCopy.Dispose();
        pixelShaderGaussian.Dispose();
        pixelShaderUpsample.Dispose();
        pixelShaderDownsample.Dispose();
        pixelShaderComposeUnderUi.Dispose();
        pixelShaderAlphaViz.Dispose();
        pixelShaderCopyZeroAlpha.Dispose();
        vertexShader.Dispose();
    }

    private const string ShaderSource = @"
Texture2D SceneTexture : register(t0);
Texture2D SharpTexture : register(t1);
Texture2D BlurTexture  : register(t2);
SamplerState SceneSampler : register(s0);

cbuffer BlurConstants : register(b0)
{
    float2 Texel;
    float  Radius;
    float  OutputAlpha;
    float  MaskStart;
    float  MaskEnd;
    float  SkipFullscreen;
    float  DirX;
    float  DirY;
    float  Strength;
    float  GrainAmount;
    float  GrainScale;
    float  GrainSoft;
    float  TintR;
    float  TintG;
    float  TintB;
    float  TintAmount;
    float  DistortAmount;
    float  DistortScale;
    float  FullscreenTest;
    float  Brightness;
    float  Saturation;
    float  Contrast;
    float  Pad0;
};

struct VsOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VsOut VsMain(uint id : SV_VertexID)
{
    VsOut o;
    o.uv  = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv.x * 2.0 - 1.0, 1.0 - o.uv.y * 2.0, 0.0, 1.0);
    return o;
}

// Passes the sampled colour through unchanged but forces alpha to 0. With a write-all blend state this resets
// the target's alpha channel to a known baseline so UI drawn afterwards accumulates coverage into it.
float4 PsCopyZeroAlpha(VsOut input) : SV_Target
{
    return float4(SceneTexture.Sample(SceneSampler, input.uv).rgb, 0.0);
}

// Diagnostic: show the sampled alpha channel as greyscale, to reveal whether the native UI writes coverage
// into the back-buffer alpha channel.
float4 PsAlphaViz(VsOut input) : SV_Target
{
    float a = SceneTexture.Sample(SceneSampler, input.uv).a;
    return float4(a, a, a, 1.0);
}

// Cheap hash -> [0,1). Stable per screen pixel, so procedural grain / bump do not shimmer over time.
float Hash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// Smooth value noise (bilinear-interpolated hash): softer frost grain and the Distortion bump field.
float ValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = Hash12(i);
    float b = Hash12(i + float2(1.0, 0.0));
    float c = Hash12(i + float2(0.0, 1.0));
    float d = Hash12(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// Compose the 'only under the UI' result.
//   t0 = the composited frame: UI drawn over the blurred scene, with per-pixel UI coverage in alpha.
//   t1 = the sharp scene (pre-UI). t2 = the blurred scene (pre-UI, before the UI drew over it).
// The composited frame is  ui_premultiplied + blurBg*(1-coverage). We re-blend only the BACKGROUND between
// sharp and blurred by a REMAPPED coverage (UI alpha < MaskStart => fully sharp bg, > MaskEnd => fully blurred
// bg, linear between), while leaving the UI itself untouched on top. Derived so the UI is never erased:
//   final = composited + (sharp - blur) * (1 - remappedCoverage) * (1 - rawCoverage)
float4 PsComposeUnderUi(VsOut input) : SV_Target
{
    float4 composited = SceneTexture.Sample(SceneSampler, input.uv);
    float3 sharp = SharpTexture.Sample(SceneSampler, input.uv).rgb;
    float3 blurred = BlurTexture.Sample(SceneSampler, input.uv).rgb;

    float rawCoverage = saturate(composited.a);
    float remapped;

    if (FullscreenTest > 0.5)
    {
        // Whole-screen test: apply blur + material everywhere, ignoring the alpha mask (no phantom / full-screen
        // gating). The UI still stays crisp on top because the material below is weighted by (1 - coverage).
        remapped = 1.0;
    }
    else
    {
        remapped = saturate((composited.a - MaskStart) / max(MaskEnd - MaskStart, 0.001));

        // Reject 'phantom' coverage: some UI image nodes write back-buffer alpha while staying visually transparent
        // (they don't change RGB) -- e.g. Bard/Astrologian/Monk job-gauge frames -- which would otherwise blur an
        // empty rectangle. Trust coverage only where the UI actually changed the (blurred) background it drew over.
        float uiChanged = smoothstep(0.0, 0.003, length(composited.rgb - blurred));
        rawCoverage *= uiChanged;
        remapped *= uiChanged;

        // Full-screen-cover skip: if the UI covers the WHOLE screen (a map / full menu / loading overlay), disable
        // the background blur (show it sharp). Detection follows ReShade KeepUIX: sample a fixed grid and treat it
        // as fullscreen only if EVERY sample point has some UI coverage (i.e. there is no fully-transparent hole).
        if (SkipFullscreen > 0.5)
        {
            const int GRID = 12;
            float minCoverage = 1.0;
            [loop] for (int gy = 0; gy < GRID; gy++)
            {
                [loop] for (int gx = 0; gx < GRID; gx++)
                {
                    float2 c = (float2(gx, gy) + 0.5) / GRID;
                    minCoverage = min(minCoverage, saturate(SceneTexture.Sample(SceneSampler, c).a));
                }
            }
            // A tiny soft band around the 'no transparent hole' threshold to avoid a 1-frame hard pop.
            float fullscreen = smoothstep(0.004, 0.02, minCoverage);
            remapped *= (1.0 - fullscreen);
        }
    }

    float3 result = composited.rgb + (sharp - blurred) * ((1.0 - remapped) * (1.0 - rawCoverage));

    // Frosted-glass material: restyle ONLY the blurred background actually visible through the UI. frostW is the
    // weight of the pre-UI blurred background in the final image (0 in the open world and under fully opaque UI;
    // positive under semi-transparent UI), so the crisp UI and the sharp world are never touched.
    float adjust = abs(Brightness - 1.0) + abs(Saturation - 1.0) + abs(Contrast - 1.0);
    float frostW = remapped * (1.0 - rawCoverage);
    if (frostW > 0.0009 && ((GrainAmount + TintAmount + DistortAmount) > 0.0 || adjust > 0.0001))
    {
        float2 pix = input.pos.xy;
        float3 styled = blurred;

        // Distortion: displace the blurred read by a procedural bump gradient (uneven-glass warp).
        if (DistortAmount > 0.0)
        {
            float2 q = pix / DistortScale;
            float n0 = ValueNoise(q);
            float2 grad = float2(ValueNoise(q + float2(1.0, 0.0)) - n0,
                                 ValueNoise(q + float2(0.0, 1.0)) - n0);
            styled = BlurTexture.Sample(SceneSampler, input.uv + grad * DistortAmount * Texel).rgb;
        }

        // Tint: recolour the frosted background toward the tint hue while preserving each pixel's luminance —
        // like a CSS sepia() filter (a tonal recolour that keeps detail), rather than a flat wash to a colour.
        float3 tintCol = float3(TintR, TintG, TintB);
        float3 lumW = float3(0.299, 0.587, 0.114);
        float3 tinted = tintCol * (dot(styled, lumW) / max(dot(tintCol, lumW), 0.001));
        styled = lerp(styled, tinted, TintAmount);

        // Background adjust: brightness, then saturation, then contrast (colour-grade the frosted background).
        if (adjust > 0.0001)
        {
            styled *= Brightness;
            float lum = dot(styled, lumW);
            styled = lerp(float3(lum, lum, lum), styled, Saturation);
            styled = (styled - 0.5) * Contrast + 0.5;
        }

        // Grain: soft value-noise (frost) or sharp quantized cells, centred so mean brightness is unchanged.
        // Both use GrainScale as the cell size — the sharp path floors to cells so the size (not just the noise
        // position) changes with it; the soft path boosts contrast so fine frost stays visible.
        if (GrainAmount > 0.0)
        {
            float2 gp = pix / GrainScale;
            float g = (GrainSoft > 0.5)
                ? (ValueNoise(gp) - 0.5) * 1.5
                : (Hash12(floor(gp)) - 0.5);
            styled += g * GrainAmount;
        }

        result += (styled - blurred) * frostW;
    }

    return float4(result, 1.0);
}

// Dual-Kawase downsample (ported from Dalamud's kawase-downsample.ps.hlsl). Halves resolution while blurring:
// centre x4 plus four diagonal taps at half-a-pixel * (1 + strength). Texel = full source-texel size.
float4 PsDownsample(VsOut input) : SV_Target
{
    float2 halfPixel = Texel * 0.5;
    float2 ofs = halfPixel * (1.0 + Strength);
    float4 sum  = SceneTexture.Sample(SceneSampler, input.uv) * 4.0;
    sum += SceneTexture.Sample(SceneSampler, input.uv - ofs);
    sum += SceneTexture.Sample(SceneSampler, input.uv + ofs);
    sum += SceneTexture.Sample(SceneSampler, input.uv + float2( ofs.x, -ofs.y));
    sum += SceneTexture.Sample(SceneSampler, input.uv + float2(-ofs.x,  ofs.y));
    return float4(sum.rgb * 0.125, OutputAlpha);
}

// Dual-Kawase upsample (ported from Dalamud's kawase-upsample.ps.hlsl). Doubles resolution while blurring:
// eight taps around the pixel (edge taps x1, corner taps x2). Texel = full source-texel size.
float4 PsUpsample(VsOut input) : SV_Target
{
    float2 hp = Texel * 0.5; // half a source texel
    float2 fp = Texel;       // one full source texel
    float ofs = 1.0 + Strength;
    float4 sum  = SceneTexture.Sample(SceneSampler, input.uv + float2(-fp.x, 0.0) * ofs);
    sum += SceneTexture.Sample(SceneSampler, input.uv + float2(-hp.x,  hp.y) * ofs) * 2.0;
    sum += SceneTexture.Sample(SceneSampler, input.uv + float2( 0.0,  fp.y) * ofs);
    sum += SceneTexture.Sample(SceneSampler, input.uv + float2( hp.x,  hp.y) * ofs) * 2.0;
    sum += SceneTexture.Sample(SceneSampler, input.uv + float2( fp.x, 0.0) * ofs);
    sum += SceneTexture.Sample(SceneSampler, input.uv + float2( hp.x, -hp.y) * ofs) * 2.0;
    sum += SceneTexture.Sample(SceneSampler, input.uv + float2( 0.0, -fp.y) * ofs);
    sum += SceneTexture.Sample(SceneSampler, input.uv + float2(-hp.x, -hp.y) * ofs) * 2.0;
    return float4(sum.rgb * (1.0 / 12.0), OutputAlpha);
}

// Separable Gaussian (9-tap, weights from ReShade KeepUIX). Dir = axis * scale, in UV. Run twice (H then V).
float4 PsGaussian(VsOut input) : SV_Target
{
    float2 axis = float2(DirX, DirY);
    float3 c  = SceneTexture.Sample(SceneSampler, input.uv).rgb * 0.227027;
    c += SceneTexture.Sample(SceneSampler, input.uv + axis * 1.0).rgb * 0.194595;
    c += SceneTexture.Sample(SceneSampler, input.uv - axis * 1.0).rgb * 0.194595;
    c += SceneTexture.Sample(SceneSampler, input.uv + axis * 2.0).rgb * 0.121622;
    c += SceneTexture.Sample(SceneSampler, input.uv - axis * 2.0).rgb * 0.121622;
    c += SceneTexture.Sample(SceneSampler, input.uv + axis * 3.0).rgb * 0.054054;
    c += SceneTexture.Sample(SceneSampler, input.uv - axis * 3.0).rgb * 0.054054;
    c += SceneTexture.Sample(SceneSampler, input.uv + axis * 4.0).rgb * 0.016216;
    c += SceneTexture.Sample(SceneSampler, input.uv - axis * 4.0).rgb * 0.016216;
    return float4(c, OutputAlpha);
}

// Plain bilinear passthrough (used for upscales that shouldn't add extra blur).
float4 PsCopy(VsOut input) : SV_Target
{
    return float4(SceneTexture.Sample(SceneSampler, input.uv).rgb, OutputAlpha);
}
";
}
