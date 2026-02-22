namespace VSILViewer.Models
{
    public class MethodViewInfo
    {
        public string TypeName { get; set; } = string.Empty;
        public string MethodName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public bool HasBody { get; set; } = true;
    }
}
