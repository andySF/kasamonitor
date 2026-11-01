using System.Buffers.Binary;
using System.Net;
using System.Text;
using FluentAssertions;
using KasaMonitor.Core.Protocol.Shelly;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Shelly;

public class ShellyMdnsTests
{
    [Fact]
    public void Query_IsOnePtrQuestionWithUnicastResponseBit()
    {
        var q = ShellyMdns.BuildQuery();

        BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(2)).Should().Be(0);   // flags: standard query
        BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(4)).Should().Be(1);   // QDCOUNT
        var name = ShellyMdns.EncodeName("_shelly._tcp.local");
        q.AsSpan(12, name.Length).ToArray().Should().Equal(name);
        BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(12 + name.Length)).Should().Be(12);       // PTR
        BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(14 + name.Length)).Should().Be(0x8001);   // QU + IN
    }

    [Fact]
    public void Response_PtrSrvTxtA_WithCompression()
    {
        var packet = BuildResponse(withA: true);

        var answer = ShellyMdns.ParseResponse(packet, IPAddress.Parse("10.9.9.9")).Should().ContainSingle().Subject;

        answer.Instance.Should().Be("shellyemg3-d885ac0b743c");
        answer.Address.Should().Be(IPAddress.Parse("192.168.254.141"));   // A record of the SRV target, not the source
        answer.Port.Should().Be(80);
        answer.Gen.Should().Be(3);
        answer.App.Should().Be("EMG3");
        answer.Txt["ver"].Should().Be("1.6.99");
    }

    [Fact]
    public void Response_WithoutARecord_UsesThePacketSource()
    {
        var answer = ShellyMdns.ParseResponse(BuildResponse(withA: false), IPAddress.Parse("192.168.254.141")).Single();

        answer.Address.Should().Be(IPAddress.Parse("192.168.254.141"));
    }

    [Fact]
    public void QueriesAndGarbage_YieldNothing()
    {
        ShellyMdns.ParseResponse(ShellyMdns.BuildQuery(), IPAddress.Loopback).Should().BeEmpty();
        ShellyMdns.ParseResponse(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0 }, IPAddress.Loopback).Should().BeEmpty();
        ShellyMdns.ParseResponse(new byte[] { 1, 2, 3 }, IPAddress.Loopback).Should().BeEmpty();
    }

    [Fact]
    public async Task Client_SendsTheQuestion_AndYieldsUnicastAnswersOnce()
    {
        using var responder = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
        var questions = 0;
        var serve = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    var q = await responder.ReceiveAsync(cts.Token);
                    Interlocked.Increment(ref questions);
                    var answer = BuildResponse(withA: true);
                    await responder.SendAsync(answer, q.RemoteEndPoint, cts.Token);   // legacy unicast answer
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
        });
        using var client = new ShellyClient(mdnsEndpoint: new IPEndPoint(IPAddress.Loopback, port),
            localAddresses: () => new[] { IPAddress.Loopback });

        var found = new List<ShellyMdnsAnswer>();
        await foreach (var a in client.DiscoverAsync(TimeSpan.FromMilliseconds(1500), CancellationToken.None)) found.Add(a);

        found.Should().ContainSingle().Which.Address.Should().Be(IPAddress.Parse("192.168.254.141"));
        questions.Should().Be(2);   // the question is repeated once after 1 s; the repeated answer is deduplicated
        responder.Dispose();
        await serve;
    }

    [Fact]
    public void Txt_KeyValuePairs()
    {
        var rdata = Txt("gen=2", "app=PlusPlugS", "flag");

        var txt = ShellyMdns.ParseTxt(rdata);

        txt.Should().Contain("gen", "2").And.Contain("app", "PlusPlugS").And.Contain("flag", "");
    }

    /// <summary>Response as a Shelly sends it: PTR answer, SRV/TXT/A additionals, names compressed against the PTR owner.</summary>
    internal static byte[] BuildResponse(bool withA)
    {
        var b = new List<byte>();
        void U16(int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }

        U16(0); U16(0x8400); U16(0); U16(1); U16(0); U16(withA ? 3 : 2);

        // PTR: _shelly._tcp.local -> shellyemg3-d885ac0b743c.<ptr owner>
        var serviceOffset = b.Count;
        b.AddRange(ShellyMdns.EncodeName("_shelly._tcp.local"));
        U16(12); U16(1); U32(120);
        var instanceLabel = Label("shellyemg3-d885ac0b743c");
        U16(instanceLabel.Length + 2);
        var instanceOffset = b.Count;
        b.AddRange(instanceLabel);
        U16(0xC000 | serviceOffset);

        // SRV: instance -> shellyemg3-d885ac0b743c.local:80
        U16(0xC000 | instanceOffset); U16(33); U16(0x8001); U32(120);
        var target = ShellyMdns.EncodeName("shellyemg3-d885ac0b743c.local");
        U16(6 + target.Length);
        U16(0); U16(0); U16(80);
        var targetOffset = b.Count;
        b.AddRange(target);

        // TXT
        U16(0xC000 | instanceOffset); U16(16); U16(0x8001); U32(120);
        var txt = Txt("gen=3", "app=EMG3", "ver=1.6.99");
        U16(txt.Length);
        b.AddRange(txt);

        if (withA)
        {
            U16(0xC000 | targetOffset); U16(1); U16(0x8001); U32(120);
            U16(4);
            b.AddRange(new byte[] { 192, 168, 254, 141 });
        }
        return b.ToArray();
    }

    private static byte[] Label(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        return new[] { (byte)bytes.Length }.Concat(bytes).ToArray();
    }

    private static byte[] Txt(params string[] entries) => entries.SelectMany(Label).ToArray();
}
