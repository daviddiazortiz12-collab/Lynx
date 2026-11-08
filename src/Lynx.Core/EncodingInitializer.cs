using System.Runtime.CompilerServices;
using System.Text;

namespace Lynx.Core
{
    internal static class EncodingInitializer
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static void EnsureRegistered()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}
