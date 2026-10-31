using System;
using System.Collections.Generic;

namespace SmartTicketing.Models
{
    // Values match 01_create_schema.sql; statuses in PurchaseTransaction are authoritative.
    public static class PaymentStatus
    {
        public const string Created="CREATED", Waiting="WAITING", Processing="PROCESSING", Confirmed="CONFIRMED";
        public const string Pending="PENDING", CancelPending="CANCEL_PENDING", Cancelled="CANCELLED";
        public const string Failed="FAILED", Expired="EXPIRED", PaidUnfulfilled="PAID_UNFULFILLED", Completed="COMPLETED";
    }
    public sealed class Route
    {
        public Guid RouteId {get;set;}
        public Guid FareRuleId {get;set;}
        public string Mode {get;set;}
        public string LineCode {get;set;}
        public string NameVi {get;set;}
        public string NameEn {get;set;}
        public string OriginVi {get;set;}
        public string DestinationVi {get;set;}
        public decimal FareVnd {get;set;}
        public override string ToString() {return Mode+" | "+LineCode+" | "+OriginVi+" → "+DestinationVi+" | "+FareVnd.ToString("N0")+" VND";}
    }
    public sealed class PurchaseSession
    {
        public Guid SessionId {get;set;}
        public Guid TvmId {get;set;}
        public Guid RouteId {get;set;}
        public decimal FareVnd {get;set;}
        public byte RetryCount {get;set;}
        public string Status {get;set;}
    }
    public sealed class PurchaseTransaction
    {
        public Guid TransactionId {get;set;}
        public Guid SessionId {get;set;}
        public int AttemptNo {get;set;}
        public string PaymentMethod {get;set;}
        public decimal AmountVnd {get;set;}
        public string Status {get;set;}
        public DateTime? SubmittedAt {get;set;}
        public DateTime? ConfirmedAt {get;set;}
        public string SupportReference {get;set;}
    }
    public sealed class PaymentAttempt
    {
        public Guid AttemptId {get;set;}
        public Guid TransactionId {get;set;}
        public string Provider {get;set;}
        public string QrPayload {get;set;}
        public DateTime? QrExpiresAt {get;set;}
    }
    public sealed class Ticket
    {
        public Guid TicketId {get;set;}
        public Guid SessionId {get;set;}
        public Guid TransactionId {get;set;}
        public string Barcode {get;set;}
        public string Status {get;set;}
    }
    public sealed class PaymentResult
    {
        public string Status {get;set;}
        public Guid TransactionId {get;set;}
        public string SupportReference {get;set;}
        public string Message {get;set;}
        public Ticket Ticket {get;set;}
        public DateTime? QrExpiresAt {get;set;}
        public string QrPayload {get;set;}
    }
}
