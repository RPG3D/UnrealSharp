namespace UnrealSharp.Core.Marshallers;

public static class EnumMarshaller<T> where T : Enum
{
    public static unsafe T FromNative(IntPtr nativeBuffer, int arrayIndex)
    {
        //byte value = BlittableMarshaller<byte>.FromNative(nativeBuffer, arrayIndex);
        byte value =  *(byte*)(nativeBuffer + arrayIndex);
        return (T) Enum.ToObject(typeof(T), value);
    }
    
    public static unsafe void ToNative(IntPtr nativeBuffer, int arrayIndex, T obj)
    {
        byte value = Convert.ToByte(obj);
        //BlittableMarshaller<byte>.ToNative(nativeBuffer, arrayIndex, value);
        *(byte*)(nativeBuffer + arrayIndex) = value;
    }
}