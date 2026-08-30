using Autodesk.Revit.DB;

namespace McpRevit.Util
{
    /// <summary>
    /// В Revit 2025 ElementId стал 64-разрядным, а IntegerValue исчез.
    /// Весь код работает с long через эти обёртки.
    /// </summary>
    public static class RevitIds
    {
        public static long ToLong(ElementId id)
        {
            if (id == null) return -1;
#if REVIT2025_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        public static ElementId FromLong(long value)
        {
#if REVIT2025_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId((int)value);
#endif
        }
    }
}
