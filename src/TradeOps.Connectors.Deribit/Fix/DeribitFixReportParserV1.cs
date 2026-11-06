using System.Globalization;
using QuickFix;
using TradeOps.Application.Oms;
using TradeOps.Domain.Enums;

namespace TradeOps.Connectors.Deribit.Fix;

internal static class DeribitFixReportParserV1
{
    internal static DeribitFixExecutionReportV1 Execution(Message m,OmsFixSessionKeyV1 session,Guid epoch,DateTimeOffset received)
    {
        Header(m,session,"8");
        if(m.IsSetField(1362)&&m.GetInt(1362)!=0)throw new InvalidOperationException("FillsGrp unexpected after ReportFillsAsExecReports=Y.");
        var side=m.GetString(54) switch{"1"=>OrderSide.Buy,"2"=>OrderSide.Sell,_=>throw new InvalidOperationException("Unknown FIX side.")};
        var report=new DeribitFixExecutionReportV1(1,session,epoch,m.Header.GetULong(34),Dup(m),Time(m.Header,52),received,Time(m,60),
            Text(m,37),Text(m,11),Text(m,41),Text(m,17),Text(m,527),m.GetString(55),side,m.GetString(39),Text(m,150),
            Number(m,38),OptionalNumber(m,152),Number(m,14),Number(m,151),m.IsSetField(854)?m.GetInt(854):null,OptionalNumber(m,231),
            m.GetString(40),OptionalNumber(m,44),Text(m,59),Text(m,18)??"",Text(m,100010),m.GetInt(103),OptionalNumber(m,32),OptionalNumber(m,31),Text(m,880));
        report.Validate();return report;
    }
    internal static DeribitFixCancelRejectV1 CancelReject(Message m,OmsFixSessionKeyV1 session,Guid epoch,DateTimeOffset received)
    {
        Header(m,session,"9");var report=new DeribitFixCancelRejectV1(1,session,epoch,m.Header.GetULong(34),Dup(m),Time(m.Header,52),received,
            Text(m,37),Text(m,11),Text(m,41),m.GetString(39),m.GetInt(102),m.GetString(434),Text(m,55),Text(m,100010));report.Validate();return report;
    }
    private static void Header(Message m,OmsFixSessionKeyV1 session,string type)
    {
        session.Validate();
        if(m.Header.GetString(8)!="FIX.4.4"||m.Header.GetString(35)!=type||m.Header.GetString(49)!=session.TargetCompId||m.Header.GetString(56)!=session.SenderCompId)
            throw new InvalidOperationException("FIX report session identity mismatch.");
    }
    private static string? Text(FieldMap map,int tag)=>map.IsSetField(tag)?map.GetString(tag):null;
    private static bool Dup(Message m)=>m.Header.IsSetField(43)&&m.Header.GetBoolean(43);
    private static decimal? OptionalNumber(FieldMap m,int tag)=>m.IsSetField(tag)?Number(m,tag):null;
    private static decimal Number(FieldMap m,int tag)
    {
        var raw=m.GetString(tag);
        if(raw.Length>32||!decimal.TryParse(raw,NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var n))
            throw new InvalidOperationException("Unqualified FIX decimal representation.");
        return n;
    }
    private static DateTimeOffset Time(FieldMap map,int tag)=>new(DateTime.SpecifyKind(map.GetDateTime(tag),DateTimeKind.Utc));
}
