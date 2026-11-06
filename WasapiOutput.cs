using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;

// Independent shared-mode WASAPI stream. Never opens or controls the game's stream.
public sealed class WasapiOutput:IDisposable {
 [DllImport("ole32.dll")]static extern int CoInitializeEx(IntPtr p,uint flags);
 [DllImport("ole32.dll")]static extern int CoCreateInstance(ref Guid cls,IntPtr outer,uint ctx,ref Guid iid,out IntPtr obj);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int Device(IntPtr s,int flow,int role,out IntPtr device);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int Activate(IntPtr s,ref Guid iid,uint ctx,IntPtr p,out IntPtr obj);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int Format(IntPtr s,out IntPtr format);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int Period(IntPtr s,IntPtr fmt,out uint def,out uint fundamental,out uint min,out uint max);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int Initialize(IntPtr s,uint flags,uint period,IntPtr fmt,IntPtr session);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int GetUint(IntPtr s,out uint value);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int SetEvent(IntPtr s,IntPtr handle);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int Service(IntPtr s,ref Guid iid,out IntPtr service);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int Simple(IntPtr s);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int GetBuffer(IntPtr s,uint frames,out IntPtr data);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int ReleaseBuffer(IntPtr s,uint frames,uint flags);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate uint Release(IntPtr s);
 static T Fn<T>(IntPtr o,int i)where T:class{return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(o),i*8),typeof(T))as T;}
 static void OK(int hr){Marshal.ThrowExceptionForHR(hr);}
 static long Now(){return (long)(DateTime.UtcNow-new DateTime(1970,1,1)).TotalMilliseconds;}
 IntPtr enumerator,device,client,render,format;readonly AutoResetEvent audioEvent=new AutoResetEvent(false);readonly ManualResetEvent rendered=new ManualResetEvent(false);
 readonly object gate=new object();readonly HashSet<string> seen=new HashSet<string>();readonly int[] positions=new int[16];
 float[] sample,mix;uint bufferFrames,periodFrames;Thread worker;volatile bool stopping;volatile string failed;volatile uint lastPadding;
 public WasapiOutput(string wave){
  for(int i=0;i<positions.Length;i++)positions[i]=-1;
  using(var r=new BinaryReader(File.OpenRead(wave))){
   if(new string(r.ReadChars(4))!="RIFF")throw new InvalidDataException("RIFF");long end=(long)r.ReadUInt32()+8;
   if(end>r.BaseStream.Length||new string(r.ReadChars(4))!="WAVE")throw new InvalidDataException("WAVE");bool valid=false;byte[] data=null;
   while(r.BaseStream.Position+8<=end){string id=new string(r.ReadChars(4));uint n=r.ReadUInt32();long next=r.BaseStream.Position+n+(n&1);if(next>end||n>16*1024*1024)throw new InvalidDataException("chunk");
    if(id=="fmt "){if(n<16)throw new InvalidDataException("fmt");ushort tag=r.ReadUInt16(),channels=r.ReadUInt16();uint rate=r.ReadUInt32();r.ReadUInt32();r.ReadUInt16();ushort bits=r.ReadUInt16();valid=channels==2&&rate==48000&&bits==32&&(tag==3||(tag==65534&&n>=40));if(tag==65534){r.BaseStream.Position=next-n+(n&1)+24;valid=valid&&r.ReadUInt32()==3;}}
    if(id=="data")data=r.ReadBytes((int)n);r.BaseStream.Position=next;
   }
   if(!valid||data==null||data.Length%8!=0)throw new InvalidDataException("Expected verified 48000Hz stereo float32 WAV");sample=new float[data.Length/4];System.Buffer.BlockCopy(data,0,sample,0,data.Length);
  }
  OK(CoInitializeEx(IntPtr.Zero,0));Guid cls=new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"),ei=new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),ci=new Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
  OK(CoCreateInstance(ref cls,IntPtr.Zero,23,ref ei,out enumerator));OK(Fn<Device>(enumerator,4)(enumerator,0,0,out device));OK(Fn<Activate>(device,3)(device,ref ci,23,IntPtr.Zero,out client));OK(Fn<Format>(client,8)(client,out format));
  if(Marshal.ReadInt16(format,2)!=2||Marshal.ReadInt32(format,4)!=48000||Marshal.ReadInt16(format,14)!=32||Marshal.ReadInt32(format,24)!=3)throw new InvalidOperationException("Current endpoint must expose 48000Hz stereo float mix format");
  uint def,fund,min,max;OK(Fn<Period>(client,18)(client,format,out def,out fund,out min,out max));periodFrames=min;OK(Fn<Initialize>(client,20)(client,0x40000,periodFrames,format,IntPtr.Zero));
  OK(Fn<GetUint>(client,4)(client,out bufferFrames));mix=new float[bufferFrames*2];OK(Fn<SetEvent>(client,13)(client,audioEvent.SafeWaitHandle.DangerousGetHandle()));Guid ri=new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");OK(Fn<Service>(client,14)(client,ref ri,out render));
  IntPtr target;OK(Fn<GetBuffer>(render,3)(render,bufferFrames,out target));OK(Fn<ReleaseBuffer>(render,4)(render,bufferFrames,2));
  worker=new Thread(RenderLoop);worker.IsBackground=true;worker.Priority=ThreadPriority.Highest;worker.Start();OK(Fn<Simple>(client,10)(client));
  if(!rendered.WaitOne(2000))throw new InvalidOperationException("Audio renderer did not start");if(failed!=null)throw new InvalidOperationException(failed);
 }
 void RenderLoop(){try{OK(CoInitializeEx(IntPtr.Zero,0));var padding=Fn<GetUint>(client,6);var get=Fn<GetBuffer>(render,3);var release=Fn<ReleaseBuffer>(render,4);
  while(!stopping){if(!audioEvent.WaitOne(1000))continue;if(stopping)break;uint used;OK(padding(client,out used));lastPadding=used;uint frames=bufferFrames-used;if(frames==0)continue;
   int count=(int)frames*2;Array.Clear(mix,0,count);lock(gate){for(int v=0;v<positions.Length;v++){int pos=positions[v];if(pos<0)continue;int n=Math.Min(count,sample.Length-pos);for(int i=0;i<n;i++)mix[i]+=sample[pos+i];pos+=n;positions[v]=pos>=sample.Length?-1:pos;}}
   for(int i=0;i<count;i++)mix[i]=Math.Max(-1,Math.Min(1,mix[i]));IntPtr data;OK(get(render,frames,out data));Marshal.Copy(mix,0,data,count);OK(release(render,frames,0));rendered.Set();
  }
 }catch(Exception e){failed=e.Message;rendered.Set();}}
 public object Play(string id,long eventTime){lock(gate){
  if(failed!=null)throw new InvalidOperationException(failed);if(seen.Contains(id))return new{kind="duplicate_ignored",id=id};
  for(int i=0;i<positions.Length;i++)if(positions[i]<0){positions[i]=0;seen.Add(id);long now=Now();return new{kind="scheduled_into_render_queue",id=id,voice=i,eventWallTimeMs=eventTime,enqueuedWallTimeMs=now,eventToQueueMs=now-eventTime,enginePeriodMs=periodFrames/48.0,bufferCapacityMs=bufferFrames/48.0,lastObservedPaddingMs=lastPadding/48.0,physicalAVLatencyMeasured=false};}
  return new{kind="voice_capacity_rejected",id=id};
 }}
 public void Dispose(){stopping=true;audioEvent.Set();if(worker!=null)worker.Join(2000);if(client!=IntPtr.Zero){try{Fn<Simple>(client,11)(client);}catch{}}foreach(IntPtr p in new[]{render,client,device,enumerator})if(p!=IntPtr.Zero)Fn<Release>(p,2)(p);if(format!=IntPtr.Zero)Marshal.FreeCoTaskMem(format);audioEvent.Dispose();rendered.Dispose();}
}
