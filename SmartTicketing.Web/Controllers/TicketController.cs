using QRCoder;
using Route = SmartTicketing.Models.Route;
using Microsoft.AspNetCore.Mvc;
using SmartTicketing.Data;
using SmartTicketing.Services;
using SmartTicketing.Models;
namespace SmartTicketing.Web.Controllers;
public sealed class TicketController : Controller
{
  private readonly TVMController _controller;
  private readonly TicketingRepository _repo;
  private readonly PaymentProcessor _payments;
  public TicketController(TVMController controller,TicketingRepository repo,PaymentProcessor payments) { _controller=controller;_repo=repo;_payments=payments; }
  private Guid SessionId => Guid.TryParse(HttpContext.Session.GetString("PurchaseSessionId"),out var v)?v:Guid.Empty;
  private Guid TransactionId => Guid.TryParse(HttpContext.Session.GetString("CurrentTransactionId"),out var v)?v:Guid.Empty;
  private IActionResult Fail(Exception ex) { TempData["Error"]=ex.Message;return RedirectToAction(nameof(Index)); }
  public IActionResult Index()
  {
    try {return View(_controller.BrowseRoutes());}
    catch(Exception ex) {ViewBag.DatabaseError=ex.Message;return View(new List<Route>());}
  }
  [HttpPost,ValidateAntiForgeryToken]
  public IActionResult Start(Guid routeId)
  {
    try {var route=_controller.BrowseRoutes().SingleOrDefault(x=>x.RouteId==routeId); if(route==null)throw new InvalidOperationException("Tuyến không hợp lệ.");var sid=_controller.StartPurchase(route);HttpContext.Session.SetString("PurchaseSessionId",sid.ToString());HttpContext.Session.Remove("CurrentTransactionId");return RedirectToAction(nameof(ChoosePayment));}
    catch(Exception ex){return Fail(ex);}
  }
  [HttpGet]
  public IActionResult ChoosePayment()
  {
    if(SessionId==Guid.Empty)return RedirectToAction(nameof(Index));
    try {ViewBag.Routes=_controller.BrowseRoutes();return View();}catch(Exception ex){return Fail(ex);}
  }
  [HttpPost,ValidateAntiForgeryToken]
  public IActionResult Begin(string method)
  {
    if(SessionId==Guid.Empty)return RedirectToAction(nameof(Index));
    try {
      if(method!="CARD"&&method!="QR")throw new InvalidOperationException("Phương thức thanh toán không hợp lệ.");
      // Nếu người dùng quay lại khi chưa gửi yêu cầu thanh toán, chỉnh phương thức
      // trên cùng TransactionId; không dùng mất lượt retry.
      PurchaseTransaction tx;
      var currentId=TransactionId;
      if(currentId!=Guid.Empty)
      {
        var current=_repo.GetPayment(currentId);
        if(current.SessionId!=SessionId)throw new InvalidOperationException("Phiên giao dịch không hợp lệ.");
        if(current.Status!=PaymentStatus.Waiting || current.SubmittedAt.HasValue)
          throw new InvalidOperationException("Không thể đổi phương thức sau khi đã gửi thanh toán.");
        _repo.ChangeUnsubmittedPaymentMethod(currentId,SessionId,method);
        tx=_repo.GetPayment(currentId);
      }
      else tx=_controller.ChoosePayment(SessionId,method,"DEMO_GATEWAY");
      HttpContext.Session.SetString("CurrentTransactionId",tx.TransactionId.ToString());
      return RedirectToAction(nameof(Pay));
    }catch(Exception ex){TempData["Error"]=ex.Message;return RedirectToAction(nameof(ChoosePayment));}
  }
  [HttpGet]
  public IActionResult Pay()
  {
    if(TransactionId==Guid.Empty)return RedirectToAction(nameof(Index));
    try {var tx=_repo.GetPayment(TransactionId);if(tx.SessionId!=SessionId)return RedirectToAction(nameof(Index));ViewBag.QrExpiresAtUtc=tx.PaymentMethod=="QR"?_repo.GetQrExpiry(tx.TransactionId):null;return View(tx);}
    catch(Exception ex){return Fail(ex);}
  }
  // QR chứa thông tin DEMO, không phải VietQR hay mã nhận tiền.
  [HttpGet]
  public IActionResult DemoQr(Guid id)
  {
    if(id==Guid.Empty || SessionId==Guid.Empty || TransactionId!=id)return NotFound();
    try
    {
      var tx=_repo.GetPayment(id);
      if(tx.SessionId!=SessionId || tx.PaymentMethod!="QR")return NotFound();
      var expiry=_repo.GetQrExpiry(id);
      if(!expiry.HasValue || expiry.Value <= DateTime.UtcNow || tx.Status!=PaymentStatus.Waiting)return NotFound();
      var payload=$"TVM-DEMO|SESSION={tx.SessionId:N}|TX={tx.TransactionId:N}|AMOUNT_VND={tx.AmountVnd:0}|NO_REAL_PAYMENT";
      using var generator=new QRCodeGenerator();
      using var qrData=generator.CreateQrCode(payload,QRCodeGenerator.ECCLevel.Q);
      var qr=new PngByteQRCode(qrData);
      return File(qr.GetGraphic(7),"image/png");
    }
    catch{return NotFound();}
  }
  [HttpPost,ValidateAntiForgeryToken]
  public IActionResult BackToPaymentMethod()
  {
    if(SessionId==Guid.Empty||TransactionId==Guid.Empty)return RedirectToAction(nameof(Index));
    try
    {
      var tx=_repo.GetPayment(TransactionId);
      if(tx.SessionId!=SessionId)throw new InvalidOperationException("Phiên giao dịch không hợp lệ.");
      if(tx.Status!=PaymentStatus.Waiting || tx.SubmittedAt.HasValue)
        throw new InvalidOperationException("Thanh toán đã được gửi hoặc đang xử lý; không thể đổi phương thức.");
      return RedirectToAction(nameof(ChoosePayment));
    }
    catch(Exception ex){TempData["Error"]=ex.Message;return RedirectToAction(nameof(Pay));}
  }
  [HttpPost,ValidateAntiForgeryToken]
  public IActionResult Submit(string scenario,bool printFailure=false)
  {
    if(TransactionId==Guid.Empty||SessionId==Guid.Empty)return RedirectToAction(nameof(Index));
    try {
      var tx=_repo.GetPayment(TransactionId);
      if(tx.SessionId!=SessionId)throw new InvalidOperationException("Phiên giao dịch không hợp lệ.");
      if(!new[]{"APPROVED","DECLINED","NO_RESPONSE"}.Contains(scenario))throw new InvalidOperationException("Kịch bản demo không hợp lệ.");
      if(tx.PaymentMethod=="QR" && tx.Status==PaymentStatus.Waiting)
      {
        var expiry=_repo.GetQrExpiry(tx.TransactionId);
        if(!expiry.HasValue || expiry.Value<=DateTime.UtcNow)
        {
          _controller.ExpireQr(tx.TransactionId);
          TempData["PaymentMessage"]="Mã QR demo đã hết hạn sau 120 giây.";
          return RedirectToAction(nameof(Result));
        }
      }
      var result=_controller.SubmitMockPayment(tx.TransactionId,scenario,true,printFailure);
      TempData["PaymentMessage"]=result.Message;
      return RedirectToAction(nameof(Result));
    }catch(Exception ex){TempData["Error"]=ex.Message;return RedirectToAction(nameof(Pay));}
  }
  [HttpPost,ValidateAntiForgeryToken]
  public IActionResult Expire()
  {
    try {var tx=_repo.GetPayment(TransactionId);if(tx.SessionId!=SessionId)throw new InvalidOperationException("Phiên giao dịch không hợp lệ.");_controller.ExpireQr(tx.TransactionId);return RedirectToAction(nameof(Result));}
    catch(Exception ex){TempData["Error"]=ex.Message;return RedirectToAction(nameof(Pay));}
  }
  [HttpGet]
  public IActionResult Result()
  {
    if(TransactionId==Guid.Empty)return RedirectToAction(nameof(Index));
    try {var tx=_repo.GetPayment(TransactionId);if(tx.SessionId!=SessionId)return RedirectToAction(nameof(Index));ViewBag.Ticket=_repo.GetTicketBySession(SessionId);return View(tx);}
    catch(Exception ex){return Fail(ex);}
  }
  [HttpPost,ValidateAntiForgeryToken]
  public IActionResult Retry()
  {
    try {var tx=_repo.GetPayment(TransactionId);if(tx.SessionId!=SessionId)throw new InvalidOperationException("Phiên giao dịch không hợp lệ.");if(tx.Status!=PaymentStatus.Failed&&tx.Status!=PaymentStatus.Expired)throw new InvalidOperationException("Chỉ thử lại sau khi thất bại hoặc QR đã hết hạn.");HttpContext.Session.Remove("CurrentTransactionId");return RedirectToAction(nameof(ChoosePayment));}
    catch(Exception ex){TempData["Error"]=ex.Message;return RedirectToAction(nameof(Result));}
  }
  [HttpPost,ValidateAntiForgeryToken]
  public IActionResult NewPurchase() {HttpContext.Session.Remove("CurrentTransactionId");HttpContext.Session.Remove("PurchaseSessionId");return RedirectToAction(nameof(Index));}
  public IActionResult Error()=>View();
}
