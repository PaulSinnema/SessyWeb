namespace SessyCommon.Attributes
{
    /// <summary>
    /// Don't copy with [destination].Copy([source]) extension.
    /// </summary>
    /// <remarks>
    /// See: MapperExtension in SessyCommon.
    /// </remarks>
    public class SkipCopyAttribute : Attribute { }
}
