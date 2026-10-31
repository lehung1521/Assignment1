using Route = SmartTicketing.Models.Route;
using ConfigurationManager = System.Configuration.ConfigurationManager;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using SmartTicketing.Models;

namespace SmartTicketing.Data
{
    public sealed class TicketingRepository
    {
        private readonly string _connection;
        public TicketingRepository(string connection=null)
        {
            _connection=connection ?? ConfigurationManager.ConnectionStrings["TVM"]?.ConnectionString;
            if(string.IsNullOrWhiteSpace(_connection)) throw new InvalidOperationException("Configure connectionStrings/TVM in App.config.");
        }
        private SqlConnection Open(){var c=new SqlConnection(_connection);c.Open();return c;}
        public Guid GetTvmId()
        {
            using(var c=Open())using(var cmd=new SqlCommand("SELECT TOP (1) TvmId FROM dbo.Tvm WHERE Code=N'TVM-DEMO-01'",c))
            {
                var obj=cmd.ExecuteScalar();if(obj==null)throw new InvalidOperationException("TVM-DEMO-01 missing: run 02_seed_data.sql");return (Guid)obj;
            }
        }
        public List<Route> GetRoutes()
        {
            var result=new List<Route>();
            using(var c=Open())using(var cmd=new SqlCommand(@"SELECT r.RouteId,r.FareRuleId,r.Mode,r.LineCode,r.RouteNameVi,r.RouteNameEn,r.OriginNameVi,r.DestinationNameVi,r.PriceVnd FROM dbo.vw_AvailableRoutes r WHERE r.OriginStationId=(SELECT OriginStationId FROM dbo.Tvm WHERE Code=N'TVM-DEMO-01') ORDER BY r.Mode,r.LineCode",c))using(var rd=cmd.ExecuteReader())
            while(rd.Read())result.Add(new Route{RouteId=rd.GetGuid(0),FareRuleId=rd.GetGuid(1),Mode=rd.GetString(2),LineCode=rd.GetString(3),NameVi=rd.GetString(4),NameEn=rd.GetString(5),OriginVi=rd.GetString(6),DestinationVi=rd.GetString(7),FareVnd=rd.GetDecimal(8)});
            return result;
        }
        public Guid CreateSession(Route route)
        {
            var id=Guid.NewGuid();
            using(var c=Open())using(var cmd=new SqlCommand(@"INSERT dbo.PurchaseSession(SessionId,TvmId,RouteId,FareRuleId,FareVnd,Language,Status) SELECT @sid,TvmId,@rid,@fid,@fare,'vi',N'ACTIVE' FROM dbo.Tvm WHERE Code=N'TVM-DEMO-01' AND SalesBlocked=0 AND Status=N'IN_SERVICE'",c))
            {
                cmd.Parameters.AddWithValue("@sid",id);cmd.Parameters.AddWithValue("@rid",route.RouteId);cmd.Parameters.AddWithValue("@fid",route.FareRuleId);cmd.Parameters.AddWithValue("@fare",route.FareVnd);if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("TVM is not available for sale");
            }
            return id;
        }
        public PurchaseTransaction CreatePayment(Guid sessionId,string method)
        {
            if(method!="CARD" && method!="QR")throw new ArgumentException("Invalid method");
            using(var c=Open())using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
            {
                // UPDLOCK serializes retries; no second payment while the previous outcome is uncertain.
                int attempts;decimal fare;string status;
                using(var q=new SqlCommand("SELECT FareVnd,Status FROM dbo.PurchaseSession WITH(UPDLOCK,HOLDLOCK) WHERE SessionId=@sid",c,tx))
                {q.Parameters.AddWithValue("@sid",sessionId);using(var rd=q.ExecuteReader()){if(!rd.Read())throw new InvalidOperationException("Session not found");fare=rd.GetDecimal(0);status=rd.GetString(1);}}
                if(status!="ACTIVE")throw new InvalidOperationException("Session already ended");
                using(var q=new SqlCommand("SELECT COUNT(1) FROM dbo.PurchaseTransaction WHERE SessionId=@sid",c,tx)){q.Parameters.AddWithValue("@sid",sessionId);attempts=Convert.ToInt32(q.ExecuteScalar());}
                if(attempts>=2)throw new InvalidOperationException("Only one fresh retry is allowed");
                if(attempts>0)
                {
                    using(var q=new SqlCommand("SELECT TOP(1) Status FROM dbo.PurchaseTransaction WHERE SessionId=@sid ORDER BY AttemptNo DESC",c,tx))
                    {q.Parameters.AddWithValue("@sid",sessionId);var old=(string)q.ExecuteScalar();if(old!=PaymentStatus.Failed && old!=PaymentStatus.Expired)throw new InvalidOperationException("Cannot retry a non-final payment");}
                }
                var payment=new PurchaseTransaction{TransactionId=Guid.NewGuid(),SessionId=sessionId,AttemptNo=attempts+1,PaymentMethod=method,AmountVnd=fare,Status=PaymentStatus.Created};
                using(var q=new SqlCommand("INSERT dbo.PurchaseTransaction(TransactionId,SessionId,AttemptNo,PaymentMethod,AmountVnd,Status) VALUES(@id,@sid,@no,@method,@amt,N'CREATED')",c,tx))
                {q.Parameters.AddWithValue("@id",payment.TransactionId);q.Parameters.AddWithValue("@sid",sessionId);q.Parameters.AddWithValue("@no",payment.AttemptNo);q.Parameters.AddWithValue("@method",method);q.Parameters.AddWithValue("@amt",fare);q.ExecuteNonQuery();}
                if(attempts>0)using(var q=new SqlCommand("UPDATE dbo.PurchaseSession SET RetryCount=1 WHERE SessionId=@sid",c,tx)){q.Parameters.AddWithValue("@sid",sessionId);q.ExecuteNonQuery();}
                tx.Commit();return payment;
            }
        }
        public PaymentAttempt CreateAttempt(Guid transactionId,string provider,string qrPayload,DateTime? expiry)
        {
            var attempt=new PaymentAttempt{AttemptId=Guid.NewGuid(),TransactionId=transactionId,Provider=provider,QrPayload=qrPayload,QrExpiresAt=expiry};
            using(var c=Open())using(var q=new SqlCommand("INSERT dbo.PaymentAttempt(AttemptId,TransactionId,Provider,QrPayload,QrGeneratedAt,QrExpiresAt) VALUES(@id,@tx,@p,@qr,@generated,@expiry)",c))
            {
                q.Parameters.AddWithValue("@id",attempt.AttemptId);q.Parameters.AddWithValue("@tx",transactionId);q.Parameters.AddWithValue("@p",provider);q.Parameters.AddWithValue("@qr",(object)qrPayload??DBNull.Value);q.Parameters.AddWithValue("@generated",qrPayload==null?(object)DBNull.Value:DateTime.UtcNow);q.Parameters.AddWithValue("@expiry",expiry.HasValue?(object)expiry.Value:DBNull.Value);q.ExecuteNonQuery();
            }
            return attempt;
        }
        // Cho phép đổi CARD/QR trên cùng TransactionId chỉ khi chưa gửi giao dịch.
        // Lock bản ghi để thao tác Back và Submit không thay đổi trạng thái cùng lúc.
        public void ChangeUnsubmittedPaymentMethod(Guid transactionId,Guid sessionId,string method)
        {
            if(method!="CARD" && method!="QR")throw new ArgumentException("Invalid payment method");
            using(var c=Open())using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
            {
                using(var q=new SqlCommand(@"UPDATE dbo.PurchaseTransaction WITH(UPDLOCK)
                    SET PaymentMethod=@method, UpdatedAt=SYSUTCDATETIME()
                    WHERE TransactionId=@id AND SessionId=@sid AND Status=N'WAITING' AND SubmittedAt IS NULL",c,tx))
                {
                    q.Parameters.AddWithValue("@method",method);
                    q.Parameters.AddWithValue("@id",transactionId);
                    q.Parameters.AddWithValue("@sid",sessionId);
                    if(q.ExecuteNonQuery()!=1)throw new InvalidOperationException("Payment has been submitted or changed; cannot switch method.");
                }
                using(var q=new SqlCommand(@"UPDATE dbo.PaymentAttempt
                    SET QrPayload=CASE WHEN @method=N'QR' THEN @qr ELSE NULL END,
                        QrGeneratedAt=CASE WHEN @method=N'QR' THEN SYSUTCDATETIME() ELSE NULL END,
                        QrExpiresAt=CASE WHEN @method=N'QR' THEN DATEADD(SECOND,120,SYSUTCDATETIME()) ELSE NULL END
                    WHERE TransactionId=@id",c,tx))
                {
                    q.Parameters.AddWithValue("@method",method);
                    q.Parameters.AddWithValue("@qr","DEMO-QR:"+transactionId.ToString("N"));
                    q.Parameters.AddWithValue("@id",transactionId);
                    if(q.ExecuteNonQuery()!=1)throw new InvalidOperationException("Gateway attempt missing.");
                }
                tx.Commit();
            }
        }
        // Thời gian hết hạn QR lưu trong cơ sở dữ liệu (UTC), không reset khi F5 trang.
        public DateTime? GetQrExpiry(Guid transactionId)
        {
            using(var c=Open())using(var q=new SqlCommand(@"SELECT TOP (1) QrExpiresAt FROM dbo.PaymentAttempt WHERE TransactionId=@id ORDER BY QrGeneratedAt DESC",c))
            {
                q.Parameters.AddWithValue("@id",transactionId);
                var value=q.ExecuteScalar();
                return value==null || value==DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)value,DateTimeKind.Utc);
            }
        }
        public PurchaseTransaction GetPayment(Guid id)
        {
            using(var c=Open())using(var q=new SqlCommand("SELECT TransactionId,SessionId,AttemptNo,PaymentMethod,AmountVnd,Status,SubmittedAt,ConfirmedAt,SupportReference FROM dbo.PurchaseTransaction WHERE TransactionId=@id",c))
            {
                q.Parameters.AddWithValue("@id",id);using(var r=q.ExecuteReader())
                {if(!r.Read())throw new InvalidOperationException("Transaction missing");return new PurchaseTransaction{TransactionId=r.GetGuid(0),SessionId=r.GetGuid(1),AttemptNo=r.GetByte(2),PaymentMethod=r.GetString(3),AmountVnd=r.GetDecimal(4),Status=r.GetString(5),SubmittedAt=r.IsDBNull(6)?(DateTime?)null:r.GetDateTime(6),ConfirmedAt=r.IsDBNull(7)?(DateTime?)null:r.GetDateTime(7),SupportReference=r.IsDBNull(8)?null:r.GetString(8)};}
            }
        }
        public void Transition(Guid id,string from,string to,string reason)
        {
            using(var c=Open())using(var tx=c.BeginTransaction())
            {
                using(var context=new SqlCommand("EXEC sys.sp_set_session_context @key=N'status_reason',@value=@reason",c,tx)){context.Parameters.AddWithValue("@reason",reason??"Demo");context.ExecuteNonQuery();}
                using(var q=new SqlCommand(@"UPDATE dbo.PurchaseTransaction SET Status=@next,UpdatedAt=SYSUTCDATETIME(),SubmittedAt=CASE WHEN @next=N'PROCESSING' AND SubmittedAt IS NULL THEN SYSUTCDATETIME() ELSE SubmittedAt END,ConfirmedAt=CASE WHEN @next=N'CONFIRMED' THEN SYSUTCDATETIME() ELSE ConfirmedAt END,SupportReference=CASE WHEN @next IN(N'PENDING',N'CANCEL_PENDING',N'PAID_UNFULFILLED') THEN COALESCE(SupportReference,@ref) ELSE SupportReference END WHERE TransactionId=@id AND Status=@previous",c,tx))
                {
                    q.Parameters.AddWithValue("@id",id);q.Parameters.AddWithValue("@previous",from);q.Parameters.AddWithValue("@next",to);q.Parameters.AddWithValue("@ref","SUP-"+id.ToString("N").Substring(0,12).ToUpperInvariant());
                    if(q.ExecuteNonQuery()!=1)throw new InvalidOperationException("Payment state changed. Refresh before continuing.");
                }
                tx.Commit();
            }
        }
        public Ticket ConfirmAndReserve(Guid id,bool passengerPresent)
        {
            using(var c=Open())using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
            {
                Guid sessionId;string status;
                using(var q=new SqlCommand("SELECT SessionId,Status FROM dbo.PurchaseTransaction WITH(UPDLOCK,HOLDLOCK) WHERE TransactionId=@id",c,tx))
                {q.Parameters.AddWithValue("@id",id);using(var r=q.ExecuteReader()){if(!r.Read())throw new InvalidOperationException("Payment missing");sessionId=r.GetGuid(0);status=r.GetString(1);}}
                if(status==PaymentStatus.Completed) {tx.Commit();return GetTicketBySession(sessionId);}
                if(status==PaymentStatus.PaidUnfulfilled){tx.Commit();return null;}
                bool late=!passengerPresent || status==PaymentStatus.Expired;
                if(status!=PaymentStatus.Processing && status!=PaymentStatus.Pending && status!=PaymentStatus.CancelPending && status!=PaymentStatus.Expired && status!=PaymentStatus.Confirmed)throw new InvalidOperationException("Cannot confirm payment in state "+status);
                if(status==PaymentStatus.Expired && !late) throw new InvalidOperationException("Invalid late payment state");
                var existing=false;
                using(var q=new SqlCommand("SELECT COUNT(1) FROM dbo.Ticket WITH(UPDLOCK,HOLDLOCK) WHERE SessionId=@sid",c,tx)){q.Parameters.AddWithValue("@sid",sessionId);existing=Convert.ToInt32(q.ExecuteScalar())>0;}
                if(existing)
                {
                    Guid ticketTransaction;
                    using(var q=new SqlCommand("SELECT TOP(1) TransactionId FROM dbo.Ticket WHERE SessionId=@sid",c,tx))
                    {q.Parameters.AddWithValue("@sid",sessionId);ticketTransaction=(Guid)q.ExecuteScalar();}
                    if(ticketTransaction==id)
                    {
                        // Duplicate SUCCESS for the same in-flight issuance: keep original state/ticket.
                        tx.Commit();return GetTicketBySession(sessionId);
                    }
                    late=true; // Another attempt already issued the unique ticket.
                }
                string final=late?PaymentStatus.PaidUnfulfilled:PaymentStatus.Confirmed;
                using(var q=new SqlCommand(@"UPDATE dbo.PurchaseTransaction SET Status=@status,ConfirmedAt=COALESCE(ConfirmedAt,SYSUTCDATETIME()),UpdatedAt=SYSUTCDATETIME(),SupportReference=CASE WHEN @status=N'PAID_UNFULFILLED' THEN COALESCE(SupportReference,@ref) ELSE SupportReference END WHERE TransactionId=@id",c,tx))
                {q.Parameters.AddWithValue("@id",id);q.Parameters.AddWithValue("@status",final);q.Parameters.AddWithValue("@ref","SUP-"+id.ToString("N").Substring(0,12).ToUpperInvariant());q.ExecuteNonQuery();}
                Ticket ticket=null;
                if(!late)
                {
                    ticket=new Ticket{TicketId=Guid.NewGuid(),SessionId=sessionId,TransactionId=id,Status="RESERVED"};ticket.Barcode=ticket.TicketId.ToString();
                    using(var q=new SqlCommand("INSERT dbo.Ticket(TicketId,SessionId,TransactionId) VALUES(@ticket,@sid,@tx)",c,tx))
                    {q.Parameters.AddWithValue("@ticket",ticket.TicketId);q.Parameters.AddWithValue("@sid",sessionId);q.Parameters.AddWithValue("@tx",id);q.ExecuteNonQuery();}
                }
                tx.Commit();return ticket;
            }
        }
        public Ticket GetTicketBySession(Guid sid)
        {
            using(var c=Open())using(var q=new SqlCommand("SELECT TicketId,SessionId,TransactionId,Barcode,Status FROM dbo.Ticket WHERE SessionId=@sid",c))
            {q.Parameters.AddWithValue("@sid",sid);using(var r=q.ExecuteReader()){if(!r.Read())return null;return new Ticket{TicketId=r.GetGuid(0),SessionId=r.GetGuid(1),TransactionId=r.GetGuid(2),Barcode=r.GetString(3),Status=r.GetString(4)};}}
        }
        public void FinishTicket(Guid transactionId,bool printed)
        {
            using(var c=Open())using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
            {
                Guid sessionId;string state;
                using(var q=new SqlCommand("SELECT SessionId,Status FROM dbo.PurchaseTransaction WITH(UPDLOCK,HOLDLOCK) WHERE TransactionId=@id",c,tx))
                {q.Parameters.AddWithValue("@id",transactionId);using(var r=q.ExecuteReader()){if(!r.Read())throw new InvalidOperationException("Transaction missing");sessionId=r.GetGuid(0);state=r.GetString(1);}}
                if(state==PaymentStatus.Completed||state==PaymentStatus.PaidUnfulfilled){tx.Commit();return;}
                if(state!=PaymentStatus.Confirmed)throw new InvalidOperationException("Payment is not confirmed");
                string ticketState=printed?"ISSUED":"PRINT_FAILED",payState=printed?PaymentStatus.Completed:PaymentStatus.PaidUnfulfilled;
                using(var q=new SqlCommand("UPDATE dbo.Ticket SET Status=@s,PrintedAt=CASE WHEN @printed=1 THEN SYSUTCDATETIME() ELSE PrintedAt END,IssuedAt=CASE WHEN @printed=1 THEN SYSUTCDATETIME() ELSE IssuedAt END WHERE TransactionId=@id",c,tx))
                {q.Parameters.AddWithValue("@s",ticketState);q.Parameters.AddWithValue("@printed",printed);q.Parameters.AddWithValue("@id",transactionId);q.ExecuteNonQuery();}
                using(var q=new SqlCommand("UPDATE dbo.PurchaseTransaction SET Status=@s,UpdatedAt=SYSUTCDATETIME(),SupportReference=CASE WHEN @printed=0 THEN COALESCE(SupportReference,@ref) ELSE SupportReference END WHERE TransactionId=@id",c,tx))
                {q.Parameters.AddWithValue("@s",payState);q.Parameters.AddWithValue("@printed",printed);q.Parameters.AddWithValue("@ref","SUP-"+transactionId.ToString("N").Substring(0,12).ToUpperInvariant());q.Parameters.AddWithValue("@id",transactionId);q.ExecuteNonQuery();}
                using(var q=new SqlCommand("UPDATE dbo.PurchaseSession SET Status=@state,EndedAt=SYSUTCDATETIME(),EndReason=@reason WHERE SessionId=@sid",c,tx))
                {q.Parameters.AddWithValue("@state",printed?"COMPLETED":"SUPPORT_REQUIRED");q.Parameters.AddWithValue("@reason",printed?"COMPLETED":"PRINTER_FAILURE");q.Parameters.AddWithValue("@sid",sessionId);q.ExecuteNonQuery();}
                if(!printed)
                {
                    using(var q=new SqlCommand("UPDATE dbo.Tvm SET SalesBlocked=1,Status=N'OUT_OF_SERVICE',BlockReason=N'PRINTER_FAILURE' WHERE TvmId=(SELECT TvmId FROM dbo.PurchaseSession WHERE SessionId=@sid)",c,tx)){q.Parameters.AddWithValue("@sid",sessionId);q.ExecuteNonQuery();}
                    using(var q=new SqlCommand("INSERT dbo.MaintenanceAlert(TvmId,AlertType,Detail,TransactionId) SELECT TvmId,N'PRINTER_FAILURE',N'Mock print failure',@tx FROM dbo.PurchaseSession WHERE SessionId=@sid",c,tx)){q.Parameters.AddWithValue("@tx",transactionId);q.Parameters.AddWithValue("@sid",sessionId);q.ExecuteNonQuery();}
                }
                tx.Commit();
            }
        }
        public void AddReconciliation(Guid id,string reason)
        {
            using(var c=Open())using(var q=new SqlCommand(@"IF NOT EXISTS(SELECT 1 FROM dbo.ReconciliationTask WHERE TransactionId=@id AND Reason=@reason AND Status=N'OPEN') INSERT dbo.ReconciliationTask(TransactionId,Reason) VALUES(@id,@reason)",c))
            {q.Parameters.AddWithValue("@id",id);q.Parameters.AddWithValue("@reason",reason);q.ExecuteNonQuery();}
        }
        public void EndSession(Guid sid,string reason)
        {
            using(var c=Open())using(var q=new SqlCommand("UPDATE dbo.PurchaseSession SET Status=N'ENDED',EndedAt=SYSUTCDATETIME(),EndReason=@reason WHERE SessionId=@sid AND Status=N'ACTIVE' AND NOT EXISTS(SELECT 1 FROM dbo.PurchaseTransaction WHERE SessionId=@sid AND Status IN(N'PENDING',N'CANCEL_PENDING',N'CONFIRMED',N'PAID_UNFULFILLED',N'COMPLETED'))",c))
            {q.Parameters.AddWithValue("@sid",sid);q.Parameters.AddWithValue("@reason",reason);q.ExecuteNonQuery();}
        }
    }
}
