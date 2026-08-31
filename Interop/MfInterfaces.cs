using System.Runtime.InteropServices;

namespace RipsawStudio.Interop;

// Every method is [PreserveSig] so HRESULTs can be inspected instead of thrown blindly:
// capture hardware fails in ways (format rejected, device yanked) that are normal control flow here.
// Vtable order below mirrors mfobjects.h / mfreadwrite.h exactly - do not reorder.

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(ref Guid key, IntPtr pValue);
    [PreserveSig] int GetItemType(ref Guid key, out int pType);
    [PreserveSig] int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int GetUINT32(ref Guid key, out uint punValue);
    [PreserveSig] int GetUINT64(ref Guid key, out ulong punValue);
    [PreserveSig] int GetDouble(ref Guid key, out double pfValue);
    [PreserveSig] int GetGUID(ref Guid key, out Guid pguidValue);
    [PreserveSig] int GetStringLength(ref Guid key, out uint pcchLength);
    [PreserveSig] int GetString(ref Guid key, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize(ref Guid key, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(ref Guid key, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, uint unValue);
    [PreserveSig] int SetUINT64(ref Guid key, ulong unValue);
    [PreserveSig] int SetDouble(ref Guid key, double fValue);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid guidValue);
    [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(ref Guid key, IntPtr pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    #region IMFAttributes
    [PreserveSig] new int GetItem(ref Guid key, IntPtr pValue);
    [PreserveSig] new int GetItemType(ref Guid key, out int pType);
    [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] new int Compare(IMFAttributes pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] new int GetUINT32(ref Guid key, out uint punValue);
    [PreserveSig] new int GetUINT64(ref Guid key, out ulong punValue);
    [PreserveSig] new int GetDouble(ref Guid key, out double pfValue);
    [PreserveSig] new int GetGUID(ref Guid key, out Guid pguidValue);
    [PreserveSig] new int GetStringLength(ref Guid key, out uint pcchLength);
    [PreserveSig] new int GetString(ref Guid key, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] new int GetAllocatedString(ref Guid key, out IntPtr ppwszValue, out uint pcchLength);
    [PreserveSig] new int GetBlobSize(ref Guid key, out uint pcbBlobSize);
    [PreserveSig] new int GetBlob(ref Guid key, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig] new int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
    [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] new int DeleteItem(ref Guid key);
    [PreserveSig] new int DeleteAllItems();
    [PreserveSig] new int SetUINT32(ref Guid key, uint unValue);
    [PreserveSig] new int SetUINT64(ref Guid key, ulong unValue);
    [PreserveSig] new int SetDouble(ref Guid key, double fValue);
    [PreserveSig] new int SetGUID(ref Guid key, ref Guid guidValue);
    [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] new int SetBlob(ref Guid key, IntPtr pBuf, uint cbBufSize);
    [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
    [PreserveSig] new int LockStore();
    [PreserveSig] new int UnlockStore();
    [PreserveSig] new int GetCount(out uint pcItems);
    [PreserveSig] new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] new int CopyAllItems(IMFAttributes pDest);
    #endregion

    [PreserveSig] int GetMajorType(out Guid pguidMajorType);
    [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
    [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
    [PreserveSig] int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
    [PreserveSig] int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
}

[ComImport, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFActivate : IMFAttributes
{
    #region IMFAttributes
    [PreserveSig] new int GetItem(ref Guid key, IntPtr pValue);
    [PreserveSig] new int GetItemType(ref Guid key, out int pType);
    [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] new int Compare(IMFAttributes pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] new int GetUINT32(ref Guid key, out uint punValue);
    [PreserveSig] new int GetUINT64(ref Guid key, out ulong punValue);
    [PreserveSig] new int GetDouble(ref Guid key, out double pfValue);
    [PreserveSig] new int GetGUID(ref Guid key, out Guid pguidValue);
    [PreserveSig] new int GetStringLength(ref Guid key, out uint pcchLength);
    [PreserveSig] new int GetString(ref Guid key, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] new int GetAllocatedString(ref Guid key, out IntPtr ppwszValue, out uint pcchLength);
    [PreserveSig] new int GetBlobSize(ref Guid key, out uint pcbBlobSize);
    [PreserveSig] new int GetBlob(ref Guid key, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig] new int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
    [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] new int DeleteItem(ref Guid key);
    [PreserveSig] new int DeleteAllItems();
    [PreserveSig] new int SetUINT32(ref Guid key, uint unValue);
    [PreserveSig] new int SetUINT64(ref Guid key, ulong unValue);
    [PreserveSig] new int SetDouble(ref Guid key, double fValue);
    [PreserveSig] new int SetGUID(ref Guid key, ref Guid guidValue);
    [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] new int SetBlob(ref Guid key, IntPtr pBuf, uint cbBufSize);
    [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
    [PreserveSig] new int LockStore();
    [PreserveSig] new int UnlockStore();
    [PreserveSig] new int GetCount(out uint pcItems);
    [PreserveSig] new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] new int CopyAllItems(IMFAttributes pDest);
    #endregion

    [PreserveSig] int ActivateObject(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample : IMFAttributes
{
    #region IMFAttributes
    [PreserveSig] new int GetItem(ref Guid key, IntPtr pValue);
    [PreserveSig] new int GetItemType(ref Guid key, out int pType);
    [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] new int Compare(IMFAttributes pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] new int GetUINT32(ref Guid key, out uint punValue);
    [PreserveSig] new int GetUINT64(ref Guid key, out ulong punValue);
    [PreserveSig] new int GetDouble(ref Guid key, out double pfValue);
    [PreserveSig] new int GetGUID(ref Guid key, out Guid pguidValue);
    [PreserveSig] new int GetStringLength(ref Guid key, out uint pcchLength);
    [PreserveSig] new int GetString(ref Guid key, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] new int GetAllocatedString(ref Guid key, out IntPtr ppwszValue, out uint pcchLength);
    [PreserveSig] new int GetBlobSize(ref Guid key, out uint pcbBlobSize);
    [PreserveSig] new int GetBlob(ref Guid key, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig] new int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
    [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] new int DeleteItem(ref Guid key);
    [PreserveSig] new int DeleteAllItems();
    [PreserveSig] new int SetUINT32(ref Guid key, uint unValue);
    [PreserveSig] new int SetUINT64(ref Guid key, ulong unValue);
    [PreserveSig] new int SetDouble(ref Guid key, double fValue);
    [PreserveSig] new int SetGUID(ref Guid key, ref Guid guidValue);
    [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] new int SetBlob(ref Guid key, IntPtr pBuf, uint cbBufSize);
    [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
    [PreserveSig] new int LockStore();
    [PreserveSig] new int UnlockStore();
    [PreserveSig] new int GetCount(out uint pcItems);
    [PreserveSig] new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] new int CopyAllItems(IMFAttributes pDest);
    #endregion

    [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
    [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
    [PreserveSig] int GetSampleTime(out long phnsSampleTime);
    [PreserveSig] int SetSampleTime(long hnsSampleTime);
    [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
    [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
    [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
    [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
    [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
}

[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
    [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
}

[ComImport, Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMF2DBuffer
{
    [PreserveSig] int Lock2D(out IntPtr pbScanline0, out int plPitch);
    [PreserveSig] int Unlock2D();
    [PreserveSig] int GetScanline0AndPitch(out IntPtr pbScanline0, out int plPitch);
    [PreserveSig] int IsContiguousFormat([MarshalAs(UnmanagedType.Bool)] out bool pfIsContiguous);
    [PreserveSig] int GetContiguousLength(out uint pcbLength);
    [PreserveSig] int ContiguousCopyTo(IntPtr pbDestBuffer, uint cbDestBuffer);
    [PreserveSig] int ContiguousCopyFrom(IntPtr pbSrcBuffer, uint cbSrcBuffer);
}

[ComImport, Guid("e7174cfa-1c9e-48b1-8866-626226bfc258"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFDXGIBuffer
{
    [PreserveSig] int GetResource(ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] int GetSubresourceIndex(out uint puSubresource);
    [PreserveSig] int GetUnknown(ref Guid guid, ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] int SetUnknown(ref Guid guid, [MarshalAs(UnmanagedType.IUnknown)] object? pUnkData);
}

[ComImport, Guid("eb533d5d-2db6-40f8-97a9-494692014f07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFDXGIDeviceManager
{
    [PreserveSig] int CloseDeviceHandle(IntPtr hDevice);
    [PreserveSig] int GetVideoService(IntPtr hDevice, ref Guid riid, out IntPtr ppService);
    [PreserveSig] int LockDevice(IntPtr hDevice, ref Guid riid, out IntPtr ppUnkDevice, [MarshalAs(UnmanagedType.Bool)] bool fBlock);
    [PreserveSig] int OpenDeviceHandle(out IntPtr phDevice);
    [PreserveSig] int ResetDevice([MarshalAs(UnmanagedType.IUnknown)] object pUnkDevice, uint resetToken);
    [PreserveSig] int TestDevice(IntPtr hDevice);
    [PreserveSig] int UnlockDevice(IntPtr hDevice, [MarshalAs(UnmanagedType.Bool)] bool fSaveState);
}

[ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] out bool pfSelected);
    [PreserveSig] int SetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);
    [PreserveSig] int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType? ppMediaType);
    [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType? ppMediaType);
    [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
    [PreserveSig] int SetCurrentPosition(ref Guid guidTimeFormat, IntPtr varPosition);
    [PreserveSig] int ReadSample(uint dwStreamIndex, uint dwControlFlags, out uint pdwActualStreamIndex,
        out uint pdwStreamFlags, out long pllTimestamp, out IMFSample? ppSample);
    [PreserveSig] int Flush(uint dwStreamIndex);
    [PreserveSig] int GetServiceForStream(uint dwStreamIndex, ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] int GetPresentationAttribute(uint dwStreamIndex, ref Guid guidAttribute, IntPtr pvarAttribute);
}

[ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    [PreserveSig] int AddStream(IMFMediaType pTargetMediaType, out uint pdwStreamIndex);
    [PreserveSig] int SetInputMediaType(uint dwStreamIndex, IMFMediaType pInputMediaType, IMFAttributes? pEncodingParameters);
    [PreserveSig] int BeginWriting();
    [PreserveSig] int WriteSample(uint dwStreamIndex, IMFSample pSample);
    [PreserveSig] int SendStreamTick(uint dwStreamIndex, long llTimestamp);
    [PreserveSig] int PlaceMarker(uint dwStreamIndex, IntPtr pvContext);
    [PreserveSig] int NotifyEndOfSegment(uint dwStreamIndex);
    [PreserveSig] int Flush(uint dwStreamIndex);
    /// <summary>IMFSinkWriter::Finalize - renamed because C# forbids calling a member named Finalize.</summary>
    [PreserveSig] int FinalizeWriting();
    [PreserveSig] int GetServiceForStream(uint dwStreamIndex, ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] int GetStatistics(uint dwStreamIndex, IntPtr pStats);
}

/// <summary>IMFMediaEventGenerator + IMFMediaSource. Only Shutdown is called; the rest hold vtable slots.</summary>
[ComImport, Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaSource
{
    [PreserveSig] int GetEvent(uint dwFlags, out IntPtr ppEvent);
    [PreserveSig] int BeginGetEvent(IntPtr pCallback, IntPtr punkState);
    [PreserveSig] int EndGetEvent(IntPtr pResult, out IntPtr ppEvent);
    [PreserveSig] int QueueEvent(uint met, ref Guid guidExtendedType, int hrStatus, IntPtr pvValue);
    [PreserveSig] int GetCharacteristics(out uint pdwCharacteristics);
    [PreserveSig] int CreatePresentationDescriptor(out IntPtr ppPresentationDescriptor);
    [PreserveSig] int Start(IntPtr pPresentationDescriptor, ref Guid pguidTimeFormat, IntPtr pvarStartPosition);
    [PreserveSig] int Stop();
    [PreserveSig] int Pause();
    [PreserveSig] int Shutdown();
}
