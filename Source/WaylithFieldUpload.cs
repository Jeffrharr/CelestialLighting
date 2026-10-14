using System.Runtime.CompilerServices;
using UnityEngine;
using Waylith.Rendering;

namespace CelestialLighting;

// Rewrites a small RGBA32 texture's pixels on RimWorld 1.7 WITHOUT reallocating it on the GPU — the
// in-place half of the vector-light field upload (VectorLightOverlay.CopyField).
//
// WHY THE ORDINARY PATH IS EXPENSIVE ON 1.7. Waylith (Ludeon's 1.7 engine) reimplements Unity's
// Texture2D in UnityStubs on top of its own Waylith.Rendering.Texture2D, and two of its calls cost far
// more than their Unity namesakes:
//
//   - GetRawTextureData<T>() is a COPY, not a view. It walks the CPU levels through a MemoryStream,
//     flips them into Unity row order, and copies the result twice (a pinned view plus a snapshot).
//   - Apply() compares the view against that snapshot, converts it back (LoadRawTextureData ->
//     ReplaceCpuData), and then, because the data is dirty, UploadLevels DISPOSES the Vulkan image and
//     calls GpuTextureService.CreateTexture2D for a new one. Every refresh is a GPU allocation.
//
// A rebuild refreshes one field per emitter, so the vector-light bake storm paid that 880 times:
// 315 ms of field upload on 1.7, against 37 ms for the whole upload stage on 1.6.
//
// WHAT THIS DOES INSTEAD. Writes the texels straight into the CPU level Waylith already holds, in
// Waylith's native row order, and hands those bytes to GpuTextureService.UploadRgbaFloat. Despite its
// name, that is a format-agnostic staging copy into the EXISTING image (vkCmdCopyBufferToImage, row
// length = width), batched into the same pending command buffer the engine's own uploads use and
// flushed before the frame's graphics submit. No allocation, no conversion, no second copy.
//
// WHY THE CPU LEVEL IS WRITTEN TOO, not only the GPU image. Waylith can drop a texture's GPU image and
// later rebuild it from the CPU copy (Texture.CanRehydrate). A GPU-only write would be silently undone
// the next time that happened, and the texture would revert to whatever the CPU copy last held — a
// stale field that still samples cleanly and still composes. Keeping the two in step also keeps
// CpuData's Dirty flag truthful: it stays clean, because nothing is pending.
//
// NARROW ON PURPOSE. TryBegin refuses anything it cannot prove is the simple case — one level, RGBA32,
// no swizzle, a live GPU image, exactly width*height texels — and the caller falls back to the Apply
// path, which is always correct. A texture that has never been uploaded has no GPU image yet, so its
// first fill always goes through Apply, which creates it.
internal static class WaylithFieldUpload
{
    // Where one field texel goes. FillField is generic over this so each destination gets its own
    // specialised loop and neither pays for the other's indexing.
    internal interface ITexelSink
    {
        void Write(int i, Color32 texel);
    }

    // Texel i of a Unity-order square written into Waylith's native-order level: same column, mirrored
    // row. Unsafe.As reinterprets four bytes of the level as one Color32 — a safe-context API, so the
    // project keeps AllowUnsafeBlocks off — and the level's length was checked in TryBegin.
    internal readonly struct NativeOrderSink : ITexelSink
    {
        private readonly byte[] level;
        private readonly int width;

        public NativeOrderSink(byte[] level, int width)
        {
            this.level = level;
            this.width = width;
        }

        public void Write(int i, Color32 texel) =>
            Unsafe.As<byte, Color32>(ref level[VectorLightMath.FlippedRowIndex(i, width) * 4]) = texel;
    }

    // One field refresh in progress: the native-order CPU level to write, and the image it belongs to.
    internal readonly struct Target
    {
        public readonly byte[] Level;
        private readonly Waylith.Rendering.Texture2D core;
        private readonly int width;
        private readonly int height;

        public Target(Waylith.Rendering.Texture2D core, byte[] level, int width, int height)
        {
            this.core = core;
            Level = level;
            this.width = width;
            this.height = height;
        }

        // Queues the GPU copy. The bytes are copied into a staging buffer when this is called, so the
        // level may be written again on the next refresh without racing the GPU.
        public void Upload()
        {
            GpuTextureService.UploadRgbaFloat(core.GpuResource, width, height, 0, Level, batch: true);
        }
    }

    internal static bool TryBegin(UnityEngine.Texture2D field, out Target target)
    {
        target = default;

        Waylith.Rendering.Texture2D core = field.CoreTexture as Waylith.Rendering.Texture2D;
        TexturePixelData cpu = core?.CpuData;

        if (!IsSimpleRgba32(core, cpu, field.width, field.height))
            return false;

        target = new Target(core, cpu.Levels[0], field.width, field.height);
        return true;
    }

    // Copies a field's current texels into one layer of the batched draw's texture array, on 1.7.
    //
    // WHY NOT Graphics.CopyTexture. UnityStubs resolves a copy's destination only as a Texture2D or a
    // RenderTexture, so a Texture2DArray slice is silently skipped: no copy, no error, and the batched
    // emitter samples whatever the slice held before. The field's CPU level is already the exact texels
    // its own GPU image holds (TryBegin's premise), so those go into the layer instead.
    //
    // NOT FLIPPED, because both images are in Waylith's native row order (row 0 at the top — see
    // NativeOrderSink). The loader's SPIR-V rewrite turns every texture sample's v into 1 - v, the
    // array's included, so a layer has to hold its rows exactly as the field's own image does.
    //
    // This was briefly flipped, and the measurement that justified it could not see the flip: the
    // batched draw's arrays were not reaching the shader then (see VectorLightShader.SetBatch), so
    // every batched emitter drew nothing and no slice orientation could change the frame. Once the
    // arrays arrived, the flipped slices measured as a mirror — a lamp low in its room drew too
    // bright above itself and too dark below, each fragment subtracting the glow from the other
    // side of the lamp.
    //
    // False when the field is not the simple case or the array has no GPU image yet; the caller
    // leaves the slot unrecorded and tries again next frame rather than claiming a copy it did not do.
    internal static bool TryUploadToLayer(
        UnityEngine.Texture2D field, UnityEngine.Texture2DArray slices, int layer)
    {
        GpuTextureAllocation array = slices.GpuTexture;

        if (array == null || field.width != slices.width || field.height != slices.height)
            return false;

        if (!TryBegin(field, out Target target))
            return false;

        // Staged before this returns, so the level is free to be rewritten by the field's next
        // refresh. TryBegin checked it is exactly width*height texels, which is the length
        // UploadRgba32ToArrayLayer copies whole.
        GpuTextureService.UploadRgba32ToArrayLayer(array, slices.width, slices.height, layer, target.Level, batch: true);
        return true;
    }

    private static bool IsSimpleRgba32(
        Waylith.Rendering.Texture2D core, TexturePixelData cpu, int width, int height)
    {
        if (core == null || cpu == null || core.GpuResource == null)
            return false;

        if (cpu.Format != LoadedTextureFormat.Rgba32 || cpu.Dirty || cpu.Swizzle != default)
            return false;

        byte[][] levels = cpu.Levels;
        return levels.Length == 1 && levels[0] != null && levels[0].Length == width * height * 4;
    }
}
