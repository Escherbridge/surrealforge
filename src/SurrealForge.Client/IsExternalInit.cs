#if NETSTANDARD2_0
// Polyfill for C# 9's init-only property syntax on netstandard2.0.
// The compiler emits a synthetic IsExternalInit modreq on init-only setters;
// targets without one (netstandard2.0) need this shim type to compile. The
// type is internal — consumers should not depend on it.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
