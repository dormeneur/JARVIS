using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace Jarvis.Platform.Overlay;

/// <summary>
/// The GPU devices every overlay layer draws with: one Direct3D device, Direct2D on top of it for
/// vector drawing, DirectWrite for text, and DirectComposition to put the result on screen.
/// Created and used on the overlay thread only.
/// </summary>
internal sealed class OverlayGraphics : IDisposable
{
    private readonly ID3D11Device _d3dDevice;

    public OverlayGraphics()
    {
        _d3dDevice = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, D3DFeatureLevel.Level_11_0, D3DFeatureLevel.Level_10_0);
        using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        Factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        Device2D = Factory.CreateDevice(dxgiDevice);
        Recorder = Device2D.CreateDeviceContext(DeviceContextOptions.None);
        Text = DWrite.DWriteCreateFactory<IDWriteFactory>();
        Composition = DComp.DCompositionCreateDevice2<IDCompositionDevice>(Device2D);
    }

    public ID2D1Factory1 Factory { get; }

    public ID2D1Device Device2D { get; }

    /// <summary>
    /// A spare context for recording command lists (effect inputs). A composition surface's own
    /// context is already mid-draw and must keep its target, so recording can't happen on it.
    /// </summary>
    public ID2D1DeviceContext Recorder { get; }

    public IDWriteFactory Text { get; }

    public IDCompositionDevice Composition { get; }

    public void Dispose()
    {
        Composition.Dispose();
        Text.Dispose();
        Recorder.Dispose();
        Device2D.Dispose();
        Factory.Dispose();
        _d3dDevice.Dispose();
    }
}
