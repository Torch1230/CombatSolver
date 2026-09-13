using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;

if (args.Length==0) throw new ArgumentException("count | fingerprint | sort [input.json]");
switch(args[0])
{
    case "count": CountChecks.Run(); break;
    case "fingerprint": FingerprintChecks.Run(); break;
    case "string": FingerprintChecks.RunString(); break;
    case "sort": SortChecks.Run(args.Skip(1).ToArray()); break;
    default:throw new ArgumentException(args[0]);
}
internal static class Measure
{
    private static object? _sink;
    public static object Run(string name,Action action,int iterations)
    {
        for(int i=0;i<Math.Min(10000,iterations);i++)action();
        long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
        for(int i=0;i<iterations;i++)action();
        return new {name,iterations,milliseconds=(Stopwatch.GetTimestamp()-start)*1000d/Stopwatch.Frequency,allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-bytes};
    }
    public static void Keep(object? value)=>_sink=value;
    public static void Print(object value)=>Console.WriteLine(JsonSerializer.Serialize(value));
}
internal static class CountChecks
{
    private static readonly Type[] Types=[typeof(int),typeof(string),typeof(decimal),typeof(long),typeof(byte),typeof(float),typeof(double),typeof(object),typeof(bool),typeof(char),typeof(short),typeof(uint),typeof(ulong),typeof(sbyte),typeof(ushort),typeof(DateTime)];
    private static Dictionary<Type,int> Clone(Dictionary<Type,int> source)
    {
        Dictionary<Type,int> result=[];foreach(var (type,count) in source)if(count!=0)result[type]=count;return result;
    }
    public static void Run()
    {
        int checks=0;Random rng=new(41512);Dictionary<Type,int> old=[];SmallTypeCounts current=new();
        for(int step=0;step<50000;step++)
        {
            Type type=Types[rng.Next(Types.Length)];
            if(rng.Next(3)!=0 || old.GetValueOrDefault(type)==0) {old[type]=old.GetValueOrDefault(type)+1;current.Increment(type);}
            else {old[type]--;current.Decrement(type);}
            if(step%37==0)
            {
                var child=current.Fork();var expected=Clone(old);
                foreach(Type t in Types){if((child?.Get(t)??0)!=expected.GetValueOrDefault(t))throw new Exception("Fork count");checks++;}
                child ??=new();child.Increment(type);
                if(current.Get(type)!=old.GetValueOrDefault(type))throw new Exception("Child modified parent");
            }
            if(step%211==0){old=Clone(old);current=current.Fork()??new();}
            if(step%997==0){old=[];current=new();}
            foreach(Type t in Types){if(current.Get(t)!=old.GetValueOrDefault(t))throw new Exception("Type count");checks++;}
        }
        Measure.Print(new {contract="random increment/decrement/zero/Fork/sibling",checks});
        foreach(int n in new[]{0,1,2,3,4,8,16})
        {
            Dictionary<Type,int> baseline=[];SmallTypeCounts candidate=new();
            for(int i=0;i<n;i++){baseline[Types[i]]=i+1;for(int j=0;j<=i;j++)candidate.Increment(Types[i]);}
            foreach(string variant in new[]{"A1","B1","B2","A2"})
                Measure.Print(Measure.Run($"fork-{n}-{variant}", variant[0]=='A'?()=>Measure.Keep(n==0?null:Clone(baseline)):()=>Measure.Keep(candidate.Fork()),200000));
        }
        Dictionary<Type,int> counter=Types.Take(3).ToDictionary(t=>t,_=>0);int index=0;
        foreach(string label in new[]{"A1","B1","B2","A2"})
            Measure.Print(Measure.Run("increment-ref-"+label,label[0]=='A'?()=>{Type type=Types[index++%3];counter[type]=counter.GetValueOrDefault(type)+1;}:()=>{Type type=Types[index++%3];CollectionsMarshal.GetValueRefOrAddDefault(counter,type,out _)++;},5000000));
    }
}
internal static class FingerprintChecks
{
    private static ulong _sink;
    private static readonly string?[] Strings=[null,"","POWER.VIGOR","PLAYER_HP","CARD.NEOWS_FURY","CARD.SLASH","MONSTER.BOWLBUG_ROCK","KnownSoul:generation:2:3:5:13:21"];
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StringBaseline(){StateFingerprintBuilder b=new();foreach(string? v in Strings)b.Add(v);_sink=b.Finish().First^b.Finish().Second;}
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StringLocal(){LocalStringFingerprintBuilder b=new();foreach(string? v in Strings)b.Add(v);_sink=b.Finish().First^b.Finish().Second;}
    private static readonly ulong[] Values=Enumerable.Range(0,256).Select(i=>unchecked((ulong)(i*7919L-800000L))).ToArray();
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Baseline(){StateFingerprintBuilder b=new();foreach(ulong v in Values)b.Add(v);_sink=b.Finish().First ^ b.Finish().Second;}
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Inline(){InlineFingerprintBuilder b=new();foreach(ulong v in Values)b.Add(v);_sink=b.Finish().First ^ b.Finish().Second;}
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Vector(){VectorFingerprintBuilder b=new();foreach(ulong v in Values)b.Add(v);_sink=b.Finish().First ^ b.Finish().Second;}
    public static void RunString()
    {
        LocalStringFingerprintBuilder local=new();StateFingerprintBuilder original=new();
        for(int i=0;i<10000;i++) { string? value=Strings[i%Strings.Length];local.Add(value);original.Add(value);if(local.Finish()!=original.Finish())throw new Exception("Local string mismatch"); }
        Measure.Print(new {stringPrefixChecks=10000});
        foreach(string label in new[]{"A1","L1","L2","A2"})
            Measure.Print(Measure.Run("string-local-"+label,label[0]=='A'?StringBaseline:StringLocal,1000000));
        GC.KeepAlive(_sink);
    }
    public static void Run()
    {
        StateFingerprintBuilder a=new();InlineFingerprintBuilder b=new();VectorFingerprintBuilder c=new();Random r=new(41512);int checks=0;
        for(int i=0;i<100000;i++)
        {
            ulong value=unchecked((ulong)r.NextInt64());a.Add(value);b.Add(value);if(Avx2.IsSupported)c.Add(value);
            if(a.Finish()!=b.Finish() || (Avx2.IsSupported && a.Finish()!=c.Finish()))throw new Exception("Fingerprint mismatch");checks++;
        }
        Measure.Print(new {contract="every prefix of scalar, inline and two-chain SIMD",checks,Avx2=Avx2.IsSupported,runtime=Environment.Version.ToString()});
        foreach(string label in new[]{"A1","I1","V1","V2","I2","A2"})
        {
            if(label[0]=='V'&&!Avx2.IsSupported)continue;
            Measure.Print(Measure.Run("fingerprint-"+label,label[0]=='A'?Baseline:label[0]=='I'?Inline:Vector,400000));
        }
        GC.KeepAlive(_sink);
    }
}
internal readonly record struct SortEntry(int Id,double Score,int Actions,int Offensive);
internal static class SortChecks
{
    private static int Compare(SortEntry left,SortEntry right)
    {
        int result=right.Score.CompareTo(left.Score);if(result!=0)return result;
        result=left.Actions.CompareTo(right.Actions);return result!=0?result:right.Offensive.CompareTo(left.Offensive);
    }
    private static readonly IComparer<SortEntry> WorstFirst=Comparer<SortEntry>.Create((a,b)=>Compare(b,a));
    private static SortEntry[] Old(SortEntry[] input,int k)
    {
        List<SortEntry> rows=new(input);rows.Sort(Compare);if(rows.Count>k)rows.RemoveRange(k,rows.Count-k);return rows.ToArray();
    }
    private static SortEntry[] Heap(SortEntry[] input,int k,bool fallback)
    {
        if(k<=0)return [];
        if(fallback)
        {
            HashSet<(double,int,int)> keys=[];
            foreach(var row in input)if(!keys.Add((row.Score,row.Actions,row.Offensive)))return Old(input,k);
        }
        PriorityQueue<SortEntry,SortEntry> heap=new(k,WorstFirst);
        foreach(var row in input)
        {
            if(heap.Count<k)heap.Enqueue(row,row);
            else if(Compare(row,heap.Peek())<0)heap.DequeueEnqueue(row,row);
        }
        List<SortEntry> result=heap.UnorderedItems.Select(p=>p.Element).ToList();result.Sort(Compare);return result.ToArray();
    }
    public static void Run(string[] paths)
    {
        List<SortEntry[]> fixtures=[];Random r=new(41512);
        for(int sample=0;sample<300;sample++)
        {
            int n=r.Next(1,1500);fixtures.Add(Enumerable.Range(0,n).Select(i=>new SortEntry(i,sample%3==0?1:sample%3==1?r.Next(8):i,r.Next(3),r.Next(3))).ToArray());
        }
        fixtures.Add(Enumerable.Range(0,80).Select(i=>new SortEntry(i,i%5==0?double.NaN:i%5==1?-0d:i%5==2?0d:i%5==3?double.PositiveInfinity:double.NegativeInfinity,0,0)).ToArray());
        int real=0,realMismatches=0,realTies=0,checks=0,heapMismatches=0,fallbackMismatches=0;
        foreach(string path in paths)
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(path));
            foreach(var row in doc.RootElement.GetProperty("deferred").EnumerateArray())
            {
                double[] scores=row.GetProperty("scores").EnumerateArray().Select(v=>v.GetDouble()).ToArray();int[] actions=row.GetProperty("actions").EnumerateArray().Select(v=>v.GetInt32()).ToArray();int[] offensive=row.GetProperty("offensive").EnumerateArray().Select(v=>v.GetInt32()).ToArray();int k=row.GetProperty("limit").GetInt32();
                var input=scores.Select((v,i)=>new SortEntry(i,v,actions[i],offensive[i])).ToArray();var expected=row.GetProperty("output").EnumerateArray().Select(v=>v.GetInt32());
                if(!Old(input,k).Select(v=>v.Id).SequenceEqual(expected))throw new Exception("Captured production sort not reproduced");
                if(!Old(input,k).SequenceEqual(Heap(input,k,false)))realMismatches++;
                if(input.Select(v=>(v.Score,v.Actions,v.Offensive)).Distinct().Count()!=input.Length)realTies++;
                fixtures.Add(input);real++;
            }
        }
        foreach(var input in fixtures)foreach(int k in new[]{0,1,2,16,128,input.Length}.Select(k=>Math.Min(input.Length,k)).Distinct())
        {
            var a=Old(input,k);if(!a.SequenceEqual(Heap(input,k,false)))heapMismatches++;
            if(!a.SequenceEqual(Heap(input,k,true)))fallbackMismatches++;checks++;
        }
        if(fallbackMismatches!=0)throw new Exception("Conservative top-k differs");
        Measure.Print(new {checks,heapMismatches,fallbackMismatches,real,realMismatches,realTies});
        foreach(int n in new[]{128,4096,32768})foreach(bool tied in new[]{false,true})
        {
            var input=Enumerable.Range(0,n).Select(i=>new SortEntry(i,tied?i%8:r.NextDouble(),0,0)).ToArray();int k=Math.Min(128,n);
            foreach(string label in new[]{"A1","B1","B2","A2"})
                Measure.Print(Measure.Run($"topk-{n}-{k}-ties{tied}-{label}",label[0]=='A'?()=>Measure.Keep(Old(input,k)):()=>Measure.Keep(Heap(input,k,true)),n>4000?300:2000));
        }
    }
}
