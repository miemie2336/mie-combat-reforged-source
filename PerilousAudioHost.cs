using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

// Windows SDK IXAudio2 ABI. Audio is decoded and resident before ready.json.
// Directory notifications carry native-game-event messages, not polling triggers.
public sealed class PerilousAudioHost : IDisposable {
#if WASAPI
 readonly WasapiOutput low;
#endif
 [StructLayout(LayoutKind.Sequential,Pack=1)] struct Wfx { public ushort tag,channels;public uint rate,avg;public ushort align,bits,extra; }
 [StructLayout(LayoutKind.Sequential,Pack=1)] struct Buffer { public uint flags,bytes;public IntPtr data;public uint begin,length,loopBegin,loopLength,loops;public IntPtr context; }
 [StructLayout(LayoutKind.Sequential,Pack=1)] struct State { public IntPtr context;public uint queued;public ulong samples; }
 [StructLayout(LayoutKind.Sequential,Pack=1)] struct Perf { public ulong audioCycles,totalCycles;public uint min,max,memory,latency,glitches,activeSources,totalSources,submix,resamplers,matrices,xmaVoices,xmaStreams; }
 [DllImport("xaudio2_9.dll",CallingConvention=CallingConvention.StdCall)] static extern int XAudio2Create(out IntPtr engine,uint flags,uint processor);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateMaster(IntPtr self,out IntPtr voice,uint channels,uint rate,uint flags,IntPtr device,IntPtr effects,uint category);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateSource(IntPtr self,out IntPtr voice,ref Wfx format,uint flags,float maxRatio,IntPtr callback,IntPtr sends,IntPtr effects);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Start(IntPtr self,uint flags,uint operation);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int StartEngine(IntPtr self);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Submit(IntPtr self,ref Buffer buffer,IntPtr wma);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetState(IntPtr self,out State state,uint flags);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetPerf(IntPtr self,out Perf data);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void Destroy(IntPtr self);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint Release(IntPtr self);
 IntPtr engine,master,pcm;readonly IntPtr[] voices=new IntPtr[8];int pcmBytes;readonly object gate=new object();
 readonly HashSet<string> seen=new HashSet<string>();readonly JavaScriptSerializer json=new JavaScriptSerializer();
 static long Now(){return (long)(DateTime.UtcNow-new DateTime(1970,1,1)).TotalMilliseconds;}
 static T Method<T>(IntPtr obj,int index) where T:class {return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj),index*IntPtr.Size),typeof(T)) as T;}
 static void Check(int hr){Marshal.ThrowExceptionForHR(hr);}
 public PerilousAudioHost(string wave){
#if WASAPI
  low=new WasapiOutput(wave);
#else
  Wfx f=new Wfx();byte[] audio=null;
  using(var r=new BinaryReader(File.OpenRead(wave))){
   if(new string(r.ReadChars(4))!="RIFF")throw new InvalidDataException("RIFF");long end=(long)r.ReadUInt32()+8;
   if(end>r.BaseStream.Length||new string(r.ReadChars(4))!="WAVE")throw new InvalidDataException("WAVE");
   while(r.BaseStream.Position+8<=end){string id=new string(r.ReadChars(4));uint n=r.ReadUInt32();long next=r.BaseStream.Position+n+(n&1);if(next>end||n>16*1024*1024)throw new InvalidDataException("chunk");
    if(id=="fmt "){if(n<16)throw new InvalidDataException("fmt");f.tag=r.ReadUInt16();f.channels=r.ReadUInt16();f.rate=r.ReadUInt32();f.avg=r.ReadUInt32();f.align=r.ReadUInt16();f.bits=r.ReadUInt16();}
    if(id=="data")audio=r.ReadBytes((int)n);r.BaseStream.Position=next;
   }
  }
  if(audio==null||audio.Length==0||f.tag!=1||f.bits!=16||f.channels!=2||f.rate!=44100||f.align!=4||f.avg!=176400)throw new InvalidDataException("Expected verified 44100 stereo PCM16");
  pcmBytes=audio.Length;pcm=Marshal.AllocHGlobal(pcmBytes);Marshal.Copy(audio,0,pcm,pcmBytes);
  Check(XAudio2Create(out engine,0,0));Check(Method<CreateMaster>(engine,7)(engine,out master,2,48000,0,IntPtr.Zero,IntPtr.Zero,6));
  for(int i=0;i<voices.Length;i++){Check(Method<CreateSource>(engine,5)(engine,out voices[i],ref f,0,2.0f,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero));Check(Method<Start>(voices[i],19)(voices[i],0,0));}
  Check(Method<StartEngine>(engine,8)(engine));
#endif
 }
 public object Play(string id,long eventMs){
#if WASAPI
  return low.Play(id,eventMs);
#else
 lock(gate){
  if(seen.Contains(id))return new {kind="duplicate_ignored",id=id};
  if(seen.Count>=20000)throw new InvalidOperationException("Event identity limit; restart host");
  for(int i=0;i<voices.Length;i++){
   State state;Method<GetState>(voices[i],25)(voices[i],out state,1);if(state.queued!=0)continue;
   var b=new Buffer{flags=0x40,bytes=(uint)pcmBytes,data=pcm};long submitted=Now();Check(Method<Submit>(voices[i],21)(voices[i],ref b,IntPtr.Zero));seen.Add(id);
   Perf p;Method<GetPerf>(engine,11)(engine,out p);
   return new {kind="submitted",id=id,voice=i,eventWallTimeMs=eventMs,submittedWallTimeMs=submitted,eventToSubmitMs=submitted-eventMs,reportedMinimumDeviceLatencyMs=p.latency/48.0,glitches=p.glitches,physicalAVLatencyMeasured=false};
  }
  return new {kind="voice_capacity_rejected",id=id};
 }
#endif
 }
 public void Dispose(){
#if WASAPI
  low.Dispose();
#else
  for(int i=0;i<voices.Length;i++)if(voices[i]!=IntPtr.Zero){Method<Destroy>(voices[i],18)(voices[i]);voices[i]=IntPtr.Zero;}if(master!=IntPtr.Zero){Method<Destroy>(master,18)(master);master=IntPtr.Zero;}if(engine!=IntPtr.Zero){Method<Release>(engine,2)(engine);engine=IntPtr.Zero;}if(pcm!=IntPtr.Zero){Marshal.FreeHGlobal(pcm);pcm=IntPtr.Zero;}
#endif
 }
 public static int Main(string[] args){
#if WASAPI
  const string backend="WASAPI";const int voiceCount=16;
#else
  const string backend="XAudio2";const int voiceCount=8;
#endif
  if(args.Length<2){Console.WriteLine("PerilousAudioHost --probe|--preview WAV OR --watch WAV DIRECTORY");return 2;}
  try{using(var host=new PerilousAudioHost(args[1])){
   if(args[0]=="--probe"){Console.WriteLine(backend+"_PRELOADED_"+voiceCount+"_VOICES_NO_PLAYBACK");return 0;}
   if(args[0]=="--preview"){Console.WriteLine(host.json.Serialize(host.Play("manual-preview",Now())));Thread.Sleep(3800);return 0;}
   if(args[0]!="--watch"||args.Length<3||args.Length>5)return 2;
   int lifetimeSeconds=args.Length==4?int.Parse(args[3]):600;
   bool resident=args.Length==5&&args[3]=="0";
   if(resident)lifetimeSeconds=0;
   if(!resident&&(lifetimeSeconds<60||lifetimeSeconds>3600))throw new ArgumentOutOfRangeException("lifetimeSeconds");
   System.Diagnostics.Process gameProcess=null;
   if(resident){gameProcess=System.Diagnostics.Process.GetProcessById(int.Parse(args[4]));if(gameProcess.ProcessName!="PBZ")throw new InvalidOperationException("Expected PBZ game process");}
   string dir=Path.GetFullPath(args[2]);Directory.CreateDirectory(dir);string log=Path.Combine(dir,"AudioHost.jsonl");
   using(var done=new ManualResetEvent(false))using(var watcher=new FileSystemWatcher(dir)){
    if(gameProcess!=null){gameProcess.EnableRaisingEvents=true;gameProcess.Exited+=(a,e)=>done.Set();if(gameProcess.HasExited)done.Set();}
    var pending=new HashSet<string>();
    Action<string> onFile=path=>{
     if(Path.GetFileName(path)=="stop.flag"){done.Set();return;}
     if(!path.EndsWith(".cue",StringComparison.OrdinalIgnoreCase))return;
     // Each notification may be processed; the event identity ledger prevents
     // duplicate playback, without suppressing the final file-close notification.
     lock(pending){pending.Add(path);}
     ThreadPool.QueueUserWorkItem(_=>{try{
      Dictionary<string,object> ev;using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){if(stream.Length>4096)return;using(var reader=new StreamReader(stream))ev=host.json.Deserialize<Dictionary<string,object>>(reader.ReadToEnd());}
      string id=Convert.ToString(ev["id"]);long timestamp=Convert.ToInt64(ev["wallTimeMs"]);
      // Reject stale and future messages. Never replay saved game events at startup.
      long age=Now()-timestamp;if(age<0||age>2000)return;
      object result=host.Play(id,timestamp);lock(host.gate)File.AppendAllText(log,host.json.Serialize(result)+Environment.NewLine);
     }catch(Exception e){lock(host.gate)File.AppendAllText(log,host.json.Serialize(new {kind="message_error",message=e.Message})+Environment.NewLine);}
     finally{lock(pending)pending.Remove(path);}});
    };
    watcher.NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.Size;watcher.Created+=(a,e)=>onFile(e.FullPath);watcher.Changed+=(a,e)=>onFile(e.FullPath);watcher.EnableRaisingEvents=true;
    File.WriteAllText(Path.Combine(dir,"ready.json"),host.json.Serialize(new {ready=true,backend=backend,pid=System.Diagnostics.Process.GetCurrentProcess().Id,resident=resident,gameProcessId=gameProcess==null?0:gameProcess.Id,readyMs=Now(),expiresMs=resident?0:Now()+lifetimeSeconds*1000L,samplePreloaded=true,voices=voiceCount,physicalAVLatencyMeasured=false}));
    if(resident)done.WaitOne();else done.WaitOne(TimeSpan.FromSeconds(lifetimeSeconds));watcher.EnableRaisingEvents=false;
   }
   if(gameProcess!=null)gameProcess.Dispose();
   File.WriteAllText(Path.Combine(dir,"ready.json"),"{\"ready\":false}");return 0;
  }}catch(Exception e){Console.Error.WriteLine(e.ToString());return 1;}
 }
}
