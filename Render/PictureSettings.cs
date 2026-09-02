namespace RipsawStudio.Render;

public enum ScalingMode { Fit, Stretch, OneToOne }

/// <summary>Forces a display aspect ratio, for sources that report the wrong one.</summary>
public enum AspectMode { Auto, Wide16x9, Standard4x3, Wide16x10 }

/// <summary>
/// How the source's luma range is interpreted. Capture cards very often send limited-range
/// (16-235) video tagged as full range, which is what makes blacks look grey.
/// </summary>
public enum RangeMode { Auto, Expand, Compress }

/// <summary>YCbCr matrix. BT.709 is correct for HD; cards sometimes tag HD as BT.601.</summary>
public enum ColorMatrix { Auto, Bt601, Bt709 }

/// <summary>
/// Picture adjustments applied by the GPU's video processor during the blit that was
/// happening anyway, so they cost no extra pass and no extra latency.
/// </summary>
public sealed class PictureSettings
{
    /// <summary>-1 to 1, neutral 0.</summary>
    public float Brightness { get; set; }
    /// <summary>0 to 2, neutral 1.</summary>
    public float Contrast { get; set; } = 1f;
    /// <summary>0 to 2, neutral 1.</summary>
    public float Saturation { get; set; } = 1f;
    public RangeMode Range { get; set; } = RangeMode.Auto;
    public ColorMatrix Matrix { get; set; } = ColorMatrix.Auto;

    /// <summary>
    /// 0 to 1, neutral 0. An edge-aware smoothing pass that runs after scaling, to take the
    /// edge off blocky/mosquito artifacts from a lossy source (e.g. a capture card's own
    /// hardware encoder) without blurring real detail. See <see cref="DeblockPass"/>.
    /// </summary>
    public float ArtifactSmoothing { get; set; }

    public PictureSettings Clone() => (PictureSettings)MemberwiseClone();
}
