// Desktop Duplication API 로 화면을 가져온다. GDI 보다 훨씬 싸다.
//
// 왜 필요한가.
//   이 PC 에서 GDI 로 화면 한 장을 가져오는 데 36ms 가 든다 (모니터 3대, 1920x1200).
//   그것만으로 초당 27장이 상한이라, 화질이나 인코딩을 아무리 손봐도 소용이 없다.
//   GDI 는 GPU 가 들고 있는 화면을 CPU 메모리로 끌어내리는데, 그 길이 느리다.
//
//   Desktop Duplication 은 윈도우(DWM)가 이미 합쳐 놓은 화면을 그대로 넘겨준다.
//   게다가 "바뀐 게 없으면" 아예 기다렸다가 없다고 알려 주므로, 가만히 있는 화면에서는
//   비용이 거의 0 이 된다.
//
// 왜 이렇게 코드가 긴가.
//   COM 인터페이스를 직접 선언해야 한다. 외부 DLL(SharpDX 등)을 쓰지 않는다는
//   이 프로젝트의 규칙 때문이다. 함수 표(vtable)는 순서가 전부라, 쓰지 않는 자리도
//   개수를 정확히 맞춰 비워 둬야 한다. 자리 하나만 어긋나도 엉뚱한 함수가 불린다.
//
// 안 되면 그냥 포기한다.
//   원격 데스크톱 세션, 화면 모드 전환, 보안 화면 전환, 오래된 드라이버 등에서
//   실패할 수 있다. 실패하면 null 을 돌려주고 부르는 쪽이 GDI 로 돌아간다.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteControl
{
    internal sealed class DesktopDup : IDisposable
    {
        // ---------------------------------------------------------------- COM 선언

        [ComImport, Guid("00000000-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUnknownX { }

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            // IDXGIObject
            void SetPrivateData_();
            void SetPrivateDataInterface_();
            void GetPrivateData_();
            void GetParent_();
            // IDXGIFactory
            void EnumAdapters_();
            void MakeWindowAssociation_();
            void GetWindowAssociation_();
            void CreateSwapChain_();
            void CreateSoftwareAdapter_();
            // IDXGIFactory1
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
            [PreserveSig] int IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            // IDXGIObject
            void SetPrivateData_();
            void SetPrivateDataInterface_();
            void GetPrivateData_();
            void GetParent_();
            // IDXGIAdapter
            [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
            void GetDesc_();
            void CheckInterfaceSupport_();
            // IDXGIAdapter1
            void GetDesc1_();
        }

        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput
        {
            // IDXGIObject
            void SetPrivateData_();
            void SetPrivateDataInterface_();
            void GetPrivateData_();
            void GetParent_();
            // IDXGIOutput
            [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
            void GetDisplayModeList_();
            void FindClosestMatchingMode_();
            void WaitForVBlank_();
            void TakeOwnership_();
            void ReleaseOwnership_();
            void GetGammaControlCapabilities_();
            void SetGammaControl_();
            void GetGammaControl_();
            void SetDisplaySurface_();
            void GetDisplaySurfaceData_();
            void GetFrameStatistics_();
        }

        [ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput1
        {
            // IDXGIObject
            void SetPrivateData_();
            void SetPrivateDataInterface_();
            void GetPrivateData_();
            void GetParent_();
            // IDXGIOutput
            void GetDesc_();
            void GetDisplayModeList_();
            void FindClosestMatchingMode_();
            void WaitForVBlank_();
            void TakeOwnership_();
            void ReleaseOwnership_();
            void GetGammaControlCapabilities_();
            void SetGammaControl_();
            void GetGammaControl_();
            void SetDisplaySurface_();
            void GetDisplaySurfaceData_();
            void GetFrameStatistics_();
            // IDXGIOutput1
            void GetDisplayModeList1_();
            void FindClosestMatchingMode1_();
            void GetDisplaySurfaceData1_();
            [PreserveSig] int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device,
                                              out IDXGIOutputDuplication dup);
        }

        [ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutputDuplication
        {
            // IDXGIObject
            void SetPrivateData_();
            void SetPrivateDataInterface_();
            void GetPrivateData_();
            void GetParent_();
            // IDXGIOutputDuplication
            void GetDesc(out DXGI_OUTDUPL_DESC desc);
            [PreserveSig] int AcquireNextFrame(uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO info,
                                               out IDXGIResource resource);
            // 배열([Out] RECT[])로 받으면 마샬러가 E_INVALIDARG 를 던진다 (실측).
            // 아래 GetFrameMoveRects 와 같은 모양으로 두고 직접 읽는다 - ABI 는 같다.
            [PreserveSig] int GetFrameDirtyRects(uint bufSize, IntPtr rects, out uint needed);
            [PreserveSig] int GetFrameMoveRects(uint bufSize, IntPtr rects, out uint needed);
            [PreserveSig] int GetFramePointerShape(uint bufSize, IntPtr buf, out uint needed, IntPtr info);
            [PreserveSig] int MapDesktopSurface(out DXGI_MAPPED_RECT mapped);
            [PreserveSig] int UnMapDesktopSurface();
            [PreserveSig] int ReleaseFrame();
        }

        [ComImport, Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIResource
        {
            // IDXGIObject
            void SetPrivateData_();
            void SetPrivateDataInterface_();
            void GetPrivateData_();
            void GetParent_();
            // IDXGIDeviceSubObject
            void GetDevice_();
            // IDXGIResource
            void GetSharedHandle_();
            void GetUsage_();
            void SetEvictionPriority_();
            void GetEvictionPriority_();
        }

        [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3D11Device
        {
            [PreserveSig] int CreateBuffer_(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int CreateTexture1D_(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int CreateTexture2D(ref D3D11_TEXTURE2D_DESC desc, IntPtr initial,
                                              out ID3D11Texture2D tex);
            // 여기부터는 쓰지 않는다. 자리만 지킨다.
            void CreateTexture3D_();
            void CreateShaderResourceView_();
            void CreateUnorderedAccessView_();
            void CreateRenderTargetView_();
            void CreateDepthStencilView_();
            void CreateInputLayout_();
            void CreateVertexShader_();
            void CreateGeometryShader_();
            void CreateGeometryShaderWithStreamOutput_();
            void CreatePixelShader_();
            void CreateHullShader_();
            void CreateDomainShader_();
            void CreateComputeShader_();
            void CreateClassLinkage_();
            void CreateBlendState_();
            void CreateDepthStencilState_();
            void CreateRasterizerState_();
            void CreateSamplerState_();
            void CreateQuery_();
            void CreatePredicate_();
            void CreateCounter_();
            void CreateDeferredContext_();
            void OpenSharedResource_();
            void CheckFormatSupport_();
            void CheckMultisampleQualityLevels_();
            void CheckCounterInfo_();
            void CheckCounter_();
            void CheckFeatureSupport_();
            void GetPrivateData_();
            void SetPrivateData_();
            void SetPrivateDataInterface_();
            void GetFeatureLevel_();
            void GetCreationFlags_();
            void GetDeviceRemovedReason_();
            void GetImmediateContext_();
            void SetExceptionMode_();
            void GetExceptionMode_();
        }

        [ComImport, Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3D11Texture2D
        {
            // ID3D11DeviceChild
            void GetDevice_();
            void GetPrivateData_();
            void SetPrivateData_();
            void SetPrivateDataInterface_();
            // ID3D11Resource
            void GetType_();
            void SetEvictionPriority_();
            void GetEvictionPriority_();
            // ID3D11Texture2D
            void GetDesc(out D3D11_TEXTURE2D_DESC desc);
        }

        // 함수 표에서 Map 은 8번째, Unmap 은 9번째, CopyResource 는 41번째다
        // (ID3D11DeviceChild 의 4개를 앞에 두고 센 값).
        [ComImport, Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3D11DeviceContext
        {
            // ID3D11DeviceChild (4)
            void GetDevice_();
            void GetPrivateData_();
            void SetPrivateData_();
            void SetPrivateDataInterface_();
            // ID3D11DeviceContext
            void VSSetConstantBuffers_();      // 1
            void PSSetShaderResources_();      // 2
            void PSSetShader_();               // 3
            void PSSetSamplers_();             // 4
            void VSSetShader_();               // 5
            void DrawIndexed_();               // 6
            void Draw_();                      // 7
            [PreserveSig] int Map(ID3D11Texture2D res, uint sub, uint mapType, uint flags,
                                  out D3D11_MAPPED_SUBRESOURCE mapped);        // 8
            void Unmap(ID3D11Texture2D res, uint sub);                          // 9
            void PSSetConstantBuffers_();      // 10
            void IASetInputLayout_();          // 11
            void IASetVertexBuffers_();        // 12
            void IASetIndexBuffer_();          // 13
            void DrawIndexedInstanced_();      // 14
            void DrawInstanced_();             // 15
            void GSSetConstantBuffers_();      // 16
            void GSSetShader_();               // 17
            void IASetPrimitiveTopology_();    // 18
            void VSSetShaderResources_();      // 19
            void VSSetSamplers_();             // 20
            void Begin_();                     // 21
            void End_();                       // 22
            void GetData_();                   // 23
            void SetPredication_();            // 24
            void GSSetShaderResources_();      // 25
            void GSSetSamplers_();             // 26
            void OMSetRenderTargets_();        // 27
            void OMSetRenderTargetsAndUAV_();  // 28
            void OMSetBlendState_();           // 29
            void OMSetDepthStencilState_();    // 30
            void SOSetTargets_();              // 31
            void DrawAuto_();                  // 32
            void DrawIndexedInstancedIndirect_(); // 33
            void DrawInstancedIndirect_();     // 34
            void Dispatch_();                  // 35
            void DispatchIndirect_();          // 36
            void RSSetState_();                // 37
            void RSSetViewports_();            // 38
            void RSSetScissorRects_();         // 39
            void CopySubresourceRegion_();     // 40
            void CopyResource(ID3D11Texture2D dst, ID3D11Texture2D src);        // 41
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public RECT DesktopCoordinates;
            [MarshalAs(UnmanagedType.Bool)] public bool AttachedToDesktop;
            public uint Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_MODE_DESC
        {
            public uint Width, Height;
            public uint RefreshNum, RefreshDen;
            public uint Format;
            public uint ScanlineOrdering, Scaling;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_DESC
        {
            public DXGI_MODE_DESC ModeDesc;
            public uint Rotation;
            [MarshalAs(UnmanagedType.Bool)] public bool DesktopImageInSystemMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_FRAME_INFO
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            [MarshalAs(UnmanagedType.Bool)] public bool RectsCoalesced;
            [MarshalAs(UnmanagedType.Bool)] public bool ProtectedContentMaskedOut;
            public uint PointerPositionX;      // POINTER_POSITION 을 펼쳐 놓은 것
            public uint PointerPositionY;
            public int PointerVisible;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_MAPPED_RECT { public int Pitch; public IntPtr Bits; }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_MAPPED_SUBRESOURCE
        {
            public IntPtr pData;
            public uint RowPitch, DepthPitch;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize, Format;
            public uint SampleCount, SampleQuality;
            public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }

        [DllImport("dxgi.dll", EntryPoint = "CreateDXGIFactory1")]
        private static extern int CreateDXGIFactory1(ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object factory);

        [DllImport("msvcrt.dll", EntryPoint = "memcpy", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr memcpy(IntPtr dst, IntPtr src, UIntPtr count);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IDXGIAdapter1 adapter, uint driverType,
            IntPtr software, uint flags, IntPtr featureLevels, uint numFeatureLevels,
            uint sdkVersion, out ID3D11Device device, out uint featureLevel,
            out ID3D11DeviceContext context);

        private const uint D3D_DRIVER_TYPE_UNKNOWN = 0;
        private const uint D3D11_SDK_VERSION = 7;
        private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

        private const uint D3D11_USAGE_STAGING = 3;
        private const uint D3D11_CPU_ACCESS_READ = 0x20000;
        private const uint D3D11_MAP_READ = 1;
        private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;

        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);

        // ---------------------------------------------------------------- 본체

        private ID3D11Device dev;
        private ID3D11DeviceContext ctx;
        private IDXGIOutputDuplication odup;
        private ID3D11Texture2D staging;
        private int srcW, srcH;
        private bool holding;              // 프레임을 잡고 있는 중인가

        // 직전에 채운 그림 크기. 바뀌면 바뀐 자리만 옮겨서는 안 된다 - 새 버퍼는 비어 있다.
        private int blitFw, blitFh;

        /// <summary>
        /// 시험용. 그림을 하나 걸어 두면 매번 그쪽은 전체를 채운다.
        /// 부분 옮기기가 정말 같은 결과인지 픽셀로 대조하려고 둔다 (bench/DirtyBench.cs).
        /// 제품에서는 항상 null 이라 비용이 없다.
        /// </summary>
        internal Bitmap Shadow = null;      // 벤치만 채운다 (CS0649 방지)

        /// <summary>이 복제본이 잡는 원본 화면 크기. 바뀐 자리 좌표가 이 기준이다.</summary>
        internal int SrcW { get { return srcW; } }
        internal int SrcH { get { return srcH; } }

        /// <summary>화면 영역에 해당하는 모니터를 찾아 복제본을 연다. 못 열면 null.</summary>
        internal static DesktopDup Open(Rectangle area)
        {
            DesktopDup d = new DesktopDup();
            try
            {
                if (d.Init(area)) return d;
            }
            catch { }

            try { d.Dispose(); } catch { }
            return null;
        }

        internal static void FindOutput(Rectangle area, out int adapter, out int output)
        {
            adapter = output = -1;
            object fo = null;
            try
            {
                Guid iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                if (CreateDXGIFactory1(ref iid, out fo) != 0) return;
                var factory = fo as IDXGIFactory1;
                if (factory == null) return;
                for (uint ai = 0; ai < 32; ai++)
                {
                    IDXGIAdapter1 ad;
                    if (factory.EnumAdapters1(ai, out ad) != 0 || ad == null) break;
                    try
                    {
                        for (uint oi = 0; oi < 32; oi++)
                        {
                            IDXGIOutput op;
                            if (ad.EnumOutputs(oi, out op) != 0 || op == null) break;
                            try
                            {
                                DXGI_OUTPUT_DESC od;
                                if (op.GetDesc(out od) == 0 && od.AttachedToDesktop && od.Rotation <= 1 &&
                                    od.DesktopCoordinates.Left == area.Left && od.DesktopCoordinates.Top == area.Top &&
                                    od.DesktopCoordinates.Right == area.Right && od.DesktopCoordinates.Bottom == area.Bottom)
                                { adapter = (int)ai; output = (int)oi; return; }
                            }
                            finally { Release(op); }
                        }
                    }
                    finally { Release(ad); }
                }
            }
            catch { adapter = output = -1; }
            finally { Release(fo); }
        }

        private bool Init(Rectangle area)
        {
            Guid iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            object fo;
            if (CreateDXGIFactory1(ref iid, out fo) != 0 || fo == null) return false;

            IDXGIFactory1 factory = fo as IDXGIFactory1;
            if (factory == null) return false;

            for (uint ai = 0; ; ai++)
            {
                IDXGIAdapter1 ad;
                if (factory.EnumAdapters1(ai, out ad) != 0 || ad == null) break;

                try
                {
                    for (uint oi = 0; ; oi++)
                    {
                        IDXGIOutput outp;
                        if (ad.EnumOutputs(oi, out outp) != 0 || outp == null) break;

                        try
                        {
                            DXGI_OUTPUT_DESC od;
                            if (outp.GetDesc(out od) != 0) continue;
                            if (!od.AttachedToDesktop) continue;

                            // 우리가 잡으려는 화면과 같은 모니터인지 본다.
                            if (od.DesktopCoordinates.Left != area.X ||
                                od.DesktopCoordinates.Top != area.Y) continue;

                            if (TryOutput(ad, outp, od)) return true;
                        }
                        finally { Release(outp); }
                    }
                }
                finally { Release(ad); }
            }

            return false;
        }

        private bool TryOutput(IDXGIAdapter1 ad, IDXGIOutput outp, DXGI_OUTPUT_DESC od)
        {
            IDXGIOutput1 o1 = outp as IDXGIOutput1;
            if (o1 == null) return false;

            uint fl;
            if (D3D11CreateDevice(ad, D3D_DRIVER_TYPE_UNKNOWN, IntPtr.Zero,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                    out dev, out fl, out ctx) != 0 || dev == null || ctx == null)
                return false;

            if (o1.DuplicateOutput(dev, out odup) != 0 || odup == null) return false;

            srcW = od.DesktopCoordinates.Right - od.DesktopCoordinates.Left;
            srcH = od.DesktopCoordinates.Bottom - od.DesktopCoordinates.Top;
            if (srcW <= 0 || srcH <= 0) return false;

            // CPU 가 읽을 수 있는 사본 자리. 여기로 복사한 뒤 픽셀을 읽는다.
            D3D11_TEXTURE2D_DESC td = new D3D11_TEXTURE2D_DESC();
            td.Width = (uint)srcW;
            td.Height = (uint)srcH;
            td.MipLevels = 1;
            td.ArraySize = 1;
            td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            td.SampleCount = 1;
            td.SampleQuality = 0;
            td.Usage = D3D11_USAGE_STAGING;
            td.BindFlags = 0;
            td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            td.MiscFlags = 0;

            return dev.CreateTexture2D(ref td, IntPtr.Zero, out staging) == 0 && staging != null;
        }

        // ---------------------------------------------------------------- 바뀐 자리
        //
        // 윈도우는 이번 프레임에서 어디가 바뀌었는지 사각형 목록으로 알려 준다.
        // 이걸 받아 두면 지문을 화면 전체에 대해 다시 계산할 필요가 없다.
        // (실측 1920x1200 전체 지문 2.4ms — 글자 한 자 쳐도 그만큼 들었다)
        //
        // 좌표는 이 모니터의 원본 픽셀 기준이다. 축소해서 보내는 중이면 부르는 쪽이
        // 자기 배율로 옮겨 써야 한다.
        private static readonly Rectangle[] NoRects = new Rectangle[0];
        private IntPtr rectMem;      // 바뀐 사각형을 받아 오는 버퍼 (재사용)
        private int rectCap;

        /// <summary>직전 Grab 에서 바뀐 사각형들 (원본 픽셀 기준).</summary>
        internal Rectangle[] Dirty = NoRects;

        /// <summary>바뀐 자리를 믿을 수 있는가. false 면 화면 전체가 바뀐 것으로 봐야 한다.</summary>
        internal bool DirtyKnown;

        // 왜 못 받았는지 벤치에서 보려고 남긴다. 제품 동작에는 쓰지 않는다.
        internal uint DbgMeta, DbgAccum, DbgNeed;
        internal double DbgAcquireMs, DbgCopyMs, DbgMapMs, DbgBlitMs;
        internal bool DbgCoalesced;
        internal int DbgHr;
        internal string DbgEx = "";

        private void ReadDirty(DXGI_OUTDUPL_FRAME_INFO info)
        {
            Dirty = NoRects;
            DirtyKnown = false;

            DbgMeta = info.TotalMetadataBufferSize;
            DbgAccum = info.AccumulatedFrames;
            DbgCoalesced = info.RectsCoalesced;
            DbgHr = 0; DbgNeed = 0; DbgEx = "";

            // 메타데이터가 없으면 알려 줄 것이 없다는 뜻이다. 전체가 바뀐 것으로 본다.
            if (info.TotalMetadataBufferSize == 0) return;

            int one = Marshal.SizeOf(typeof(RECT));

            try
            {
                // 메타데이터 크기는 dirty + move 를 합친 값이라 넉넉히 잡는다.
                int cap = (int)info.TotalMetadataBufferSize;
                if (cap < one) cap = one;
                if (cap > 1 << 20) cap = 1 << 20;

                if (rectMem == IntPtr.Zero || rectCap < cap)
                {
                    if (rectMem != IntPtr.Zero) Marshal.FreeHGlobal(rectMem);
                    rectMem = Marshal.AllocHGlobal(cap);
                    rectCap = cap;
                }

                uint need;
                int hr = odup.GetFrameMoveRects((uint)rectCap, rectMem, out need);
                DbgHr = hr; DbgNeed = need;
                var regions = new System.Collections.Generic.List<Rectangle>();
                if (hr != 0 || !DesktopRegions.Append(rectMem, need, rectCap, true, srcW, srcH, regions)) return;
                hr = odup.GetFrameDirtyRects((uint)rectCap, rectMem, out need);
                DbgHr = hr; DbgNeed = need;
                if (hr != 0 || !DesktopRegions.Append(rectMem, need, rectCap, false, srcW, srcH, regions)) return;
                Dirty = regions.ToArray();
                DirtyKnown = true;
            }
            catch (Exception ex) { DbgEx = ex.GetType().Name + ": " + ex.Message; }
        }

        internal const int GrabFailed = 0;      // 못 쓰겠다. GDI 로 돌아가라
        internal const int GrabNoChange = 1;    // 화면이 안 바뀌었다. 그림은 그대로다
        internal const int GrabNewFrame = 2;    // 새 그림을 채웠다

        /// <summary>
        /// 한 장 가져와 frame 에 채운다.
        ///
        /// "안 바뀌었다" 를 따로 알려 주는 것이 이 방식의 진짜 이점이다.
        /// GDI 는 화면이 그대로여도 매번 36ms 를 들여 통째로 읽고, 그 뒤에 타일 지문을
        /// 전부 다시 계산해야 바뀐 게 없다는 걸 알 수 있었다.
        /// 여기서는 윈도우가 "새 그림 없음" 이라고 바로 알려 주므로 둘 다 건너뛴다.
        /// </summary>
        internal int Grab(Bitmap frame, Rectangle area, int fw, int fh)
        {
            if (odup == null || staging == null) return GrabFailed;

            DXGI_OUTDUPL_FRAME_INFO info;
            IDXGIResource res = null;

            if (holding) { try { odup.ReleaseFrame(); } catch { } holding = false; }

            System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
            int hr = odup.AcquireNextFrame(12, out info, out res);
            DbgAcquireMs = _sw.Elapsed.TotalMilliseconds;

            if (hr == DXGI_ERROR_WAIT_TIMEOUT) return GrabNoChange;
            if (hr != 0 || res == null) throw new InvalidOperationException("AcquireNextFrame 0x" + hr.ToString("X8"));

            holding = true;

            try
            {
                ID3D11Texture2D tex = res as ID3D11Texture2D;
                if (tex == null) return GrabFailed;

                // AccumulatedFrames 가 0 이면 그림은 그대로고 마우스만 움직인 것이다.
                // 커서는 따로 보내므로 여기서는 할 일이 없다.
                if (info.AccumulatedFrames == 0 && info.LastPresentTime == 0) return GrabNoChange;

                // 어디가 바뀌었는지 윈도우가 이미 알고 있다. 프레임을 놓기 전에 받아 둔다.
                ReadDirty(info);

                _sw.Restart();
                ctx.CopyResource(staging, tex);
                DbgCopyMs = _sw.Elapsed.TotalMilliseconds;

                _sw.Restart();
                D3D11_MAPPED_SUBRESOURCE map;
                if (ctx.Map(staging, 0, D3D11_MAP_READ, 0, out map) != 0 || map.pData == IntPtr.Zero)
                    return GrabFailed;
                DbgMapMs = _sw.Elapsed.TotalMilliseconds;

                _sw.Restart();
                try { Blit(map.pData, (int)map.RowPitch, frame, fw, fh); }
                finally { ctx.Unmap(staging, 0); }
                DbgBlitMs = _sw.Elapsed.TotalMilliseconds;

                return GrabNewFrame;
            }
            finally
            {
                Release(res);
                try { odup.ReleaseFrame(); } catch { }
                holding = false;
            }
        }

        /// <summary>
        /// GPU 에서 받은 픽셀을 우리 그림으로 옮긴다. 크기가 다르면 줄이면서 옮긴다.
        /// 줄이는 방식은 "필요한 픽셀만 골라 쓰기" 다. 곱게 섞으면 느려서 얻는 것보다 잃는다.
        /// </summary>
        private void Blit(IntPtr src, int srcPitch, Bitmap frame, int fw, int fh)
        {
            BlitBody(src, srcPitch, frame, fw, fh);
        }

        private void BlitBody(IntPtr src, int srcPitch, Bitmap frame, int fw, int fh)
        {
            // 바뀐 자리만 옮긴다. 그림 버퍼는 계속 쓰는 것이라 나머지는 이미 맞는 내용이다.
            // 실측 1920x1200 전체 옮기기 1.0ms - 글자 한 자 쳤을 때 그만큼이 통째로 낭비였다.
            //
            // 다만 그림 크기가 바뀌었으면(축소 배율 변경 등) 새 버퍼가 비어 있으므로
            // 반드시 전부 채워야 한다. 안 그러면 안 바뀐 자리가 검은 채로 남는다.
            bool all = !DirtyKnown || Dirty.Length == 0 || blitFw != fw || blitFh != fh;
            blitFw = fw; blitFh = fh;

            // 대조용 그림에는 언제나 전체를 채운다.
            if (Shadow != null) BlitInto(src, srcPitch, Shadow, fw, fh, true);

            BlitInto(src, srcPitch, frame, fw, fh, all);
        }

        private void BlitInto(IntPtr src, int srcPitch, Bitmap frame, int fw, int fh, bool all)
        {
            BitmapData bd = frame.LockBits(new Rectangle(0, 0, fw, fh),
                                           ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* s0 = (byte*)src;
                    byte* d0 = (byte*)bd.Scan0;

                    if (fw == srcW && fh == srcH)
                    {
                        // Buffer.MemoryCopy 은 .NET 4.6 부터다. 이 프로젝트는 4.0 이라 직접 부른다.
                        if (all)
                        {
                            int bytes = fw * 4;
                            for (int y = 0; y < fh; y++)
                                memcpy(new IntPtr(d0 + (long)y * bd.Stride),
                                       new IntPtr(s0 + (long)y * srcPitch), (UIntPtr)(uint)bytes);
                            return;
                        }

                        foreach (Rectangle q in Dirty)
                        {
                            int x0 = q.Left   < 0 ? 0 : q.Left;
                            int y0 = q.Top    < 0 ? 0 : q.Top;
                            int x1 = q.Right  > fw ? fw : q.Right;
                            int y1 = q.Bottom > fh ? fh : q.Bottom;
                            if (x1 <= x0 || y1 <= y0) continue;

                            int bytes = (x1 - x0) * 4;
                            for (int y = y0; y < y1; y++)
                                memcpy(new IntPtr(d0 + (long)y * bd.Stride + x0 * 4),
                                       new IntPtr(s0 + (long)y * srcPitch + x0 * 4), (UIntPtr)(uint)bytes);
                        }
                        return;
                    }

                    // 미리 계산해 두면 안쪽 반복문이 곱셈 없이 돈다.
                    int[] xmap = new int[fw];
                    for (int x = 0; x < fw; x++) xmap[x] = (int)((long)x * srcW / fw);

                    if (all)
                    {
                        for (int y = 0; y < fh; y++)
                        {
                            int sy = (int)((long)y * srcH / fh);
                            int* sr = (int*)(s0 + (long)sy * srcPitch);
                            int* dr = (int*)(d0 + (long)y * bd.Stride);

                            for (int x = 0; x < fw; x++) dr[x] = sr[xmap[x]];
                        }
                        return;
                    }

                    // 줄인 그림에서는 바뀐 자리를 그림 좌표로 옮겨야 한다.
                    // 가장자리가 잘리지 않도록 바깥쪽으로 한 칸씩 넉넉히 잡는다.
                    foreach (Rectangle q in Dirty)
                    {
                        int x0 = (int)((long)q.Left * fw / srcW);
                        int y0 = (int)((long)q.Top  * fh / srcH);
                        int x1 = (int)(((long)q.Right  * fw + srcW - 1) / srcW) + 1;
                        int y1 = (int)(((long)q.Bottom * fh + srcH - 1) / srcH) + 1;

                        if (x0 < 0) x0 = 0;
                        if (y0 < 0) y0 = 0;
                        if (x1 > fw) x1 = fw;
                        if (y1 > fh) y1 = fh;
                        if (x1 <= x0 || y1 <= y0) continue;

                        for (int y = y0; y < y1; y++)
                        {
                            int sy = (int)((long)y * srcH / fh);
                            int* sr = (int*)(s0 + (long)sy * srcPitch);
                            int* dr = (int*)(d0 + (long)y * bd.Stride);

                            for (int x = x0; x < x1; x++) dr[x] = sr[xmap[x]];
                        }
                    }
                }
            }
            finally { frame.UnlockBits(bd); }
        }

        private static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o))
            {
                try { Marshal.ReleaseComObject(o); } catch { }
            }
        }

        public void Dispose()
        {
            if (holding && odup != null) { try { odup.ReleaseFrame(); } catch { } }
            holding = false;

            Release(staging); staging = null;
            Release(odup); odup = null;
            Release(ctx); ctx = null;
            Release(dev); dev = null;
        }
    }
}
