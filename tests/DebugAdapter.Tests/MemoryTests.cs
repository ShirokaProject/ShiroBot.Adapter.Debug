using System.Text.Json;
using ShiroBot.Adapter.Debug;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using Xunit;
public sealed class MemoryTests
{
    [Fact]
    public async Task TupleMockPreservesFilesAndHistoryCanBeCalled()
    {
        var config = new DebugConfig();
        await using var runtime = new DebugRuntime(config, _=>{});
        var services = new DebugServices(()=>runtime,()=>config);
        var methods = new MethodCatalog(runtime,services,config);
        var catalog = JsonSerializer.SerializeToElement(methods.Describe(),runtime.Catalog.Json);
        var history = catalog.EnumerateArray().First(x=>x.GetProperty("method").GetString()=="GetHistoryMessagesAsync");
        var call = history.GetProperty("example").Deserialize<MethodCall>(runtime.Catalog.Json)!;
        var response = await methods.CallAsync(call, default);
        JsonSerializer.Serialize(response,runtime.Catalog.Json);
        var files = catalog.EnumerateArray().First(x=>x.GetProperty("method").GetString()=="GetGroupFilesAsync");
        methods.SetResult(new(files.GetProperty("methodId").GetString()!,JsonSerializer.SerializeToElement(new {item1=new[]{new{groupId="123",fileId="file",fileName="fixture.txt"}},item2=Array.Empty<object>()})));
        var api = (IQFileApi)methods.Resolve(typeof(IQFileApi))!;
        var result = await api.GetGroupFilesAsync("123");
        Assert.Single(result.Files);Assert.Equal("fixture.txt",result.Files[0].FileName);Assert.Empty(result.Folders);
    }
    [Fact]
    public async Task OversizedSnapshotsAvoidPayloadSizedAllocationsAndKeepSmallJsonExact()
    {
        var config = new DebugConfig {MaxRecordBytes=1024};
        await using var runtime = new DebugRuntime(config,_=>{});
        var payload=new {uri="base64://"+new string('A',4*1024*1024)};
        runtime.Record("outbound","image",payload);
        long before=GC.GetTotalAllocatedBytes(true);
        for(int i=0;i<10;i++)Assert.True(runtime.Record("outbound","image",payload).Data.GetProperty("truncated").GetBoolean());
        long allocated=GC.GetTotalAllocatedBytes(true)-before;
        Assert.True(allocated<1024*1024,$"Snapshot allocated {allocated} bytes for already-oversized strings.");
        Assert.False(runtime.FitsSnapshot(payload));
        var small=new {text="中文\\\"\n",dict=new Dictionary<string,string>{{"key","value"}}};
        Assert.Equal(JsonSerializer.Serialize(small,runtime.Catalog.Json),runtime.Record("outbound","small",small).Data.GetRawText());
        Assert.True(runtime.FitsSnapshot(small));
    }
}
