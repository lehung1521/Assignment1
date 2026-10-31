using Route = SmartTicketing.Models.Route;
using System;
using SmartTicketing.Models;
using SmartTicketing.Data;

namespace SmartTicketing.Services
{
    public sealed class PricingEngine
    {
        public decimal Calculate(Route route) {if(route==null||route.FareVnd<=0)throw new ArgumentException("Invalid route");return route.FareVnd;}
    }
    public interface IPaymentGateway
    {
        // This is simulation only; real MoMo/VNPAY/ZaloPay integrations require server-side credentials, signed callbacks and actual provider APIs.
        string SimulateFinalResult(string scenario);
    }
    public sealed class GatewayAdapter : IPaymentGateway
    {
        public string SimulateFinalResult(string scenario)
        {
            if(scenario=="APPROVED")return "SUCCESS";
            if(scenario=="DECLINED")return "FAILED";
            if(scenario=="NO_RESPONSE")return "PENDING";
            throw new ArgumentException("Unknown mock scenario");
        }
    }
    public interface IPrinterDevice {bool Print(Ticket ticket);}
    public sealed class PrinterService : IPrinterDevice
    {
        public bool SimulateFailure {get;set;}
        public bool Print(Ticket ticket) {if(ticket==null)throw new ArgumentNullException("ticket");return !SimulateFailure;}
    }
    public sealed class PaymentProcessor
    {
        private readonly TicketingRepository _repository;
        private readonly IPaymentGateway _gateway;
        private readonly IPrinterDevice _printer;
        public PaymentProcessor(TicketingRepository repository,IPaymentGateway gateway,IPrinterDevice printer)
        {_repository=repository;_gateway=gateway;_printer=printer;}
        public PurchaseTransaction Begin(Guid sessionId,string method,string provider)
        {
            var tx=_repository.CreatePayment(sessionId,method);
            string qr=method=="QR"?"DEMO-QR:"+tx.TransactionId.ToString("N"):null;
            _repository.CreateAttempt(tx.TransactionId,provider,qr,method=="QR"?(DateTime?)DateTime.UtcNow.AddSeconds(120):null);
            _repository.Transition(tx.TransactionId,PaymentStatus.Created,PaymentStatus.Waiting,"Gateway payment attempt prepared");
            return tx;
        }
        public PaymentResult FinalizeMock(Guid transactionId,string scenario,bool passengerPresent,bool printFailure)
        {
            var tx=_repository.GetPayment(transactionId);
            if(tx.Status==PaymentStatus.Completed || tx.Status==PaymentStatus.PaidUnfulfilled)
                return new PaymentResult{TransactionId=transactionId,Status=tx.Status,SupportReference=tx.SupportReference,Message="Already processed"};
            if(tx.Status==PaymentStatus.Waiting)_repository.Transition(transactionId,PaymentStatus.Waiting,PaymentStatus.Processing,"Mock card submitted / QR paid");
            tx=_repository.GetPayment(transactionId);
            var result=_gateway.SimulateFinalResult(scenario);
            if(result=="PENDING")
            {
                if(tx.Status==PaymentStatus.Processing)_repository.Transition(transactionId,PaymentStatus.Processing,PaymentStatus.Pending,"No final response within mock 30s");
                _repository.AddReconciliation(transactionId,"PENDING");
                var p=_repository.GetPayment(transactionId);
                return new PaymentResult{TransactionId=transactionId,Status=p.Status,SupportReference=p.SupportReference,Message="Pending reconciliation: no retry while unresolved"};
            }
            if(result=="FAILED")
            {
                if(tx.Status==PaymentStatus.Processing || tx.Status==PaymentStatus.Pending)_repository.Transition(transactionId,tx.Status,PaymentStatus.Failed,"Verified mock decline");
                else throw new InvalidOperationException("Cannot decline a final transaction");
                return new PaymentResult{TransactionId=transactionId,Status=PaymentStatus.Failed,Message="Payment declined; one retry allowed"};
            }
            var ticket=_repository.ConfirmAndReserve(transactionId,passengerPresent);
            if(ticket==null)
            {
                var late=_repository.GetPayment(transactionId);_repository.AddReconciliation(transactionId,"LATE_SUCCESS");
                return new PaymentResult{TransactionId=transactionId,Status=late.Status,SupportReference=late.SupportReference,Message="Verified late payment; manual support required"};
            }
            var printer=_printer as PrinterService;if(printer!=null)printer.SimulateFailure=printFailure;
            bool printed=_printer.Print(ticket);
            _repository.FinishTicket(transactionId,printed);
            var final=_repository.GetPayment(transactionId);
            return new PaymentResult{TransactionId=transactionId,Status=final.Status,SupportReference=final.SupportReference,Ticket=ticket,Message=printed?"Ticket issued successfully":"Printer failure; support required"};
        }
        public PaymentResult ExpireQr(Guid id)
        {
            var p=_repository.GetPayment(id);
            if(p.PaymentMethod!="QR"||p.Status!=PaymentStatus.Waiting)throw new InvalidOperationException("Only an unpaid waiting QR may expire");
            _repository.Transition(id,PaymentStatus.Waiting,PaymentStatus.Expired,"QR expired after 120s without submitted payment");
            return new PaymentResult{TransactionId=id,Status=PaymentStatus.Expired,Message="QR expired; one fresh attempt permitted"};
        }
        public PaymentResult CancelBeforeSubmission(Guid id)
        {
            var p=_repository.GetPayment(id);
            if(p.Status!=PaymentStatus.Created)throw new InvalidOperationException("Submitted or waiting gateway payments must be verified/voided first; not silently cancelled");
            _repository.Transition(id,PaymentStatus.Created,PaymentStatus.Cancelled,"Cancelled before gateway request");
            return new PaymentResult{TransactionId=id,Status=PaymentStatus.Cancelled};
        }
        public PaymentResult ConfirmLateMock(Guid id)
        {
            var t=_repository.ConfirmAndReserve(id,false);
            if(t!=null)throw new InvalidOperationException("Late callback should not create a ticket");
            _repository.AddReconciliation(id,"LATE_SUCCESS");
            var p=_repository.GetPayment(id);
            return new PaymentResult{TransactionId=id,Status=p.Status,SupportReference=p.SupportReference,Message="Late verified success recorded without printing"};
        }
    }
    public sealed class ReconciliationService
    {
        private readonly PaymentProcessor _payments;
        public ReconciliationService(PaymentProcessor payments){_payments=payments;}
        public PaymentResult ApplyVerifiedLateSuccess(Guid transactionId){return _payments.ConfirmLateMock(transactionId);}
        // Real background reconciliation is not implemented; gateway mock has no remote status endpoint.
    }
    public sealed class TVMController
    {
        private readonly TicketingRepository _repository;
        private readonly PaymentProcessor _payments;
        public TVMController(TicketingRepository repo,PaymentProcessor payments){_repository=repo;_payments=payments;}
        public System.Collections.Generic.List<Route> BrowseRoutes(){return _repository.GetRoutes();}
        public Guid StartPurchase(Route route){if(route==null)throw new ArgumentNullException("route");return _repository.CreateSession(route);}
        public PurchaseTransaction ChoosePayment(Guid sessionId,string method,string provider){return _payments.Begin(sessionId,method,provider);}
        public PaymentResult SubmitMockPayment(Guid txId,string scenario,bool passengerPresent,bool simulatePrintFailure){return _payments.FinalizeMock(txId,scenario,passengerPresent,simulatePrintFailure);}
        public PaymentResult ExpireQr(Guid txId){return _payments.ExpireQr(txId);}
        public void CancelUnpaidSession(Guid sid){_repository.EndSession(sid,"CANCELLED");}
    }
}
