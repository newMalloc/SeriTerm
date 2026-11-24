using System.Runtime.InteropServices;
using Windows.Graphics.Effects;
using WinRT;

namespace SeriTerm.App.Services;

// Direct2D effect descriptions consumed by Windows.UI.Composition, in native property order.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal abstract class BackdropEffect(string name, IGraphicsEffectSource source) :
    IGraphicsEffect, IGraphicsEffectSource, IGraphicsEffectD2D1Interop, ICustomQueryInterface
{
    public string Name { get; set; } = name;
    public abstract Guid GetEffectId();
    public int GetPropertyCount() => 3;
    public int GetSourceCount() => 1;
    public nint GetSource(int index) => index == 0 ? MarshalInterface<IGraphicsEffectSource>.FromManaged(source)
        : throw new ArgumentOutOfRangeException(nameof(index));

    protected abstract object GetPropertyValue(int index);
    protected abstract uint GetPropertyIndex(string name);

    public nint GetProperty(int index)
    {
        var boxed = MarshalInspectable<object>.FromManaged(GetPropertyValue(index));
        try
        {
            var iid = new Guid("4BD682DD-7554-40E9-9A9B-82654EDE7E62"); // Windows.Foundation.IPropertyValue
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(boxed, ref iid, out var property));
            return property;
        }
        finally { Marshal.Release(boxed); }
    }

    public void GetNamedPropertyMapping(string name, out uint index, out uint mapping)
    {
        index = GetPropertyIndex(name);
        mapping = 1; // GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT
    }

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint result)
    {
        result = 0;
        if (iid != typeof(IGraphicsEffectD2D1Interop).GUID) return CustomQueryInterfaceResult.NotHandled;
        result = Marshal.GetComInterfaceForObject(this, typeof(IGraphicsEffectD2D1Interop), CustomQueryInterfaceMode.Ignore);
        return CustomQueryInterfaceResult.Handled;
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class BackdropGaussianBlur(IGraphicsEffectSource source) : BackdropEffect("GlassBlur", source)
{
    public override Guid GetEffectId() => new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");

    protected override object GetPropertyValue(int index) => index switch
    {
        0 => (object)28f, // StandardDeviation in compositor pixels
        1 => 1u, // balanced optimization
        2 => 1u, // hard border
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    protected override uint GetPropertyIndex(string name) => name switch
    {
        "BlurAmount" => 0,
        "Optimization" => 1,
        "BorderMode" => 2,
        _ => throw new ArgumentException(nameof(name)),
    };
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class BackdropTone(IGraphicsEffectSource source, bool dark) : BackdropEffect("GlassTone", source)
{
    public override Guid GetEffectId() => new("921F03D6-641C-47DF-852D-B4BB6153AE11");

    protected override object GetPropertyValue(int index) => index switch
    {
        0 => CreateMatrix(),
        1 => 1u, // premultiplied alpha
        2 => true, // clamp output to the displayable range
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    protected override uint GetPropertyIndex(string name) => name switch
    {
        "ColorMatrix" => 0,
        "AlphaMode" => 1,
        "ClampOutput" => 2,
        _ => throw new ArgumentException(nameof(name)),
    };

    private float[] CreateMatrix()
    {
        // Each output channel = base + luminance * neutralGain + (channel - luminance) * colorGain.
        // Flatten white and gray shapes around the theme's base while retaining the hue of the blurred scene.
        var (baseTone, neutralGain, colorGain) = dark ? (0.21f, 0.043f, 0.94f) : (0.935f, 0.04f, 0.24f);
        var neutralCorrection = neutralGain - colorGain;
        var r = 0.30f * neutralCorrection;
        var g = 0.59f * neutralCorrection;
        var b = 0.11f * neutralCorrection;
        return
        [
            r + colorGain, r,             r,             0,
            g,             g + colorGain, g,             0,
            b,             b,             b + colorGain, 0,
            0,             0,             0,             1,
            baseTone,      baseTone,      baseTone,      0,
        ];
    }
}

[ComVisible(true), Guid("2FC57384-A068-44D7-A331-30982FCF7177"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IGraphicsEffectD2D1Interop
{
    Guid GetEffectId();
    void GetNamedPropertyMapping([MarshalAs(UnmanagedType.LPWStr)] string name, out uint index, out uint mapping);
    int GetPropertyCount();
    nint GetProperty(int index);
    nint GetSource(int index);
    int GetSourceCount();
}
