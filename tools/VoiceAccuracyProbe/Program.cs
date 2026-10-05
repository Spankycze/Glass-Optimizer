using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;
using SpankyhoOptimizer.Services;

string output = Path.GetFullPath(args[0]);
string phase = args[1];
Directory.CreateDirectory(output);
try
{
    byte[] whole = await Fixture("whole", "Jakou teplotu má moje grafika?");
    byte[] first = await Fixture("first", "Jakou teplotu");
    byte[] second = await Fixture("second", "má moje grafika?");
    byte[] paused = first.Concat(new byte[12800]).Concat(second).ToArray();
    byte[] quiet = ScaleToQuiet(whole, 0.009);
    var reports = new List<object>();
    bool allReady = true;
    foreach (var item in new[] { (Name:"normal", Pcm:whole), (Name:"pause400",Pcm:paused), (Name:"quiet",Pcm:quiet) })
    {
        await using var service = new GeminiSegmentTranscriptionService();
        var transcripts = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Set("_apiKey", VoiceCloudAiSettingsService.GetApiKey());
        Set("_onTranscript", new Action<string>(x => transcripts.Enqueue(x)));
        Set("_cts", new CancellationTokenSource());
        typeof(GeminiSegmentTranscriptionService).GetProperty("IsRunning")!.SetValue(service, true);
        byte[] pcm = new byte[6400 + item.Pcm.Length + 48000];
        Buffer.BlockCopy(item.Pcm,0,pcm,6400,item.Pcm.Length);
        var feed = typeof(GeminiSegmentTranscriptionService).GetMethod("OnAudioAvailable", BindingFlags.NonPublic|BindingFlags.Instance)!;
        for(int pos=0;pos<pcm.Length;pos+=3200)
        {
            byte[] chunk=new byte[3200];
            Buffer.BlockCopy(pcm,pos,chunk,0,Math.Min(3200,pcm.Length-pos));
            feed.Invoke(service,new object?[]{null,new WaveInEventArgs(chunk,chunk.Length)});
        }
        await Task.Delay(100);
        DateTime deadline=DateTime.UtcNow.AddSeconds(25);
        while(Get("_activeTranscriptionCts") != null && DateTime.UtcNow<deadline) await Task.Delay(50);
        string transcript=string.Join(" ",transcripts);
        int segments=(int)Get("_transcriptionGeneration")!;
        string[] expected=Words("Jakou teplotu má moje grafika?");
        string[] actual=Words(transcript);
        double wer=Distance(expected,actual)/(double)expected.Length;
        bool complete=actual.Contains("jakou") && actual.Contains("teplotu") && actual.Any(x=>x.StartsWith("grafik",StringComparison.Ordinal));
        allReady &= complete && segments==1;
        reports.Add(new {name=item.Name,segments,transcript,wer,complete});
        Console.WriteLine($"ACCURACY phase={phase} case={item.Name} segments={segments} complete={complete} wer={wer:0.00}");
        void Set(string name,object value)=>typeof(GeminiSegmentTranscriptionService).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(service,value);
        object? Get(string name)=>typeof(GeminiSegmentTranscriptionService).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(service);
    }
    File.WriteAllText(Path.Combine(output,phase+"-accuracy.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
    if(phase=="after" && !allReady) Environment.ExitCode=2;
}
finally { await NeuralTtsWorkerService.StopAsync(); }

async Task<byte[]> Fixture(string name,string text)
{
    string pcmPath=Path.Combine(output,name+".pcm");
    if(File.Exists(pcmPath)) return File.ReadAllBytes(pcmPath);
    string mp3=Path.Combine(output,name+".mp3");
    await NeuralTtsWorkerService.GenerateAsync(text,mp3,CancellationToken.None);
    using var reader=new MediaFoundationReader(mp3);
    using var resampler=new MediaFoundationResampler(reader,new WaveFormat(16000,16,1));
    using var memory=new MemoryStream();
    byte[] buffer=new byte[3200];
    int read;
    while((read=resampler.Read(buffer,0,buffer.Length))>0) memory.Write(buffer,0,read);
    byte[] data=memory.ToArray();
    int begin=0,end=data.Length;
    while(begin+640<end && Rms(data,begin,640)<0.003) begin+=640;
    while(end-640>begin && Rms(data,end-640,640)<0.003) end-=640;
    byte[] trimmed=data[begin..end];
    File.WriteAllBytes(pcmPath,trimmed);
    File.Delete(mp3);
    return trimmed;
}
static double Rms(byte[] data,int offset,int count)
{
    double sum=0;for(int i=offset;i<offset+count;i+=2){double x=BitConverter.ToInt16(data,i)/32768d;sum+=x*x;}
    return Math.Sqrt(sum/(count/2));
}
static byte[] ScaleToQuiet(byte[] data,double level)
{
    var levels=new List<double>();
    for(int i=0;i+3200<=data.Length;i+=3200) levels.Add(Rms(data,i,3200));
    levels.Sort();
    double gain=level/levels[(int)((levels.Count-1)*0.95)];
    byte[] quiet=new byte[data.Length];
    for(int i=0;i<data.Length;i+=2){short value=(short)Math.Clamp(Math.Round(BitConverter.ToInt16(data,i)*gain),short.MinValue,short.MaxValue);quiet[i]=(byte)value;quiet[i+1]=(byte)(value>>8);}
    return quiet;
}
static string[] Words(string text)
{
    var b=new StringBuilder();
    foreach(char c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        if(CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark) b.Append(c);
    return Regex.Matches(b.ToString(),@"[\p{L}\p{N}]+").Select(x=>x.Value).ToArray();
}
static int Distance(string[] a,string[] b)
{
    int[] row=Enumerable.Range(0,b.Length+1).ToArray();
    for(int i=1;i<=a.Length;i++){int previous=row[0];row[0]=i;for(int j=1;j<=b.Length;j++){int saved=row[j];row[j]=Math.Min(Math.Min(row[j]+1,row[j-1]+1),previous+(a[i-1]==b[j-1]?0:1));previous=saved;}}
    return row[b.Length];
}
