namespace Sparky;

internal unsafe class UnsafeHelpers
{
    public static void CopyToList<T>(T* nativeArray, uint count, List<T> destination)
        where T : unmanaged
    {
        destination.Clear();
        
        for (var i = 0; i < count; i++)
        {
            T data = *(nativeArray + i);
            destination.Add(data);
        }
    }
}