using System;
using System.Drawing;
using System.Windows.Forms;
using SmartTicketing.Models;
using SmartTicketing.Data;
using SmartTicketing.Services;

namespace SmartTicketingSystem
{
    public sealed class TicketMachineForm : Form
    {
        private readonly ComboBox routes=new ComboBox(),method=new ComboBox(),scenario=new ComboBox();
        private readonly Label status=new Label(),paymentDetails=new Label();
        private readonly CheckBox printerFailure=new CheckBox();
        private readonly Button pay=new Button(),expire=new Button(),retry=new Button(),late=new Button(),start=new Button();
        private TVMController controller;private PaymentProcessor processor;
        private Guid sessionId=Guid.Empty, transactionId=Guid.Empty;
        private int retryCount;
        public TicketMachineForm()
        {
            Text="Smart Ticketing System - C# / MSSQL Demo";ClientSize=new Size(920,640);StartPosition=FormStartPosition.CenterScreen;
            Font=new Font("Segoe UI",11);BackColor=Color.WhiteSmoke;
            var title=new Label{Text="TVM • SMART TICKETING SYSTEM",Location=new Point(30,18),Size=new Size(840,40),Font=new Font("Segoe UI",19,FontStyle.Bold),ForeColor=Color.DarkSlateBlue};Controls.Add(title);
            AddLabel("Route (Bus / MRT)",30,78);routes.Location=new Point(30,107);routes.Width=820;routes.DropDownStyle=ComboBoxStyle.DropDownList;Controls.Add(routes);
            AddLabel("Payment method",30,164);method.Location=new Point(30,191);method.Width=280;method.DropDownStyle=ComboBoxStyle.DropDownList;method.Items.AddRange(new object[]{"CARD","QR"});method.SelectedIndex=0;Controls.Add(method);
            AddLabel("Mock gateway scenario",340,164);scenario.Location=new Point(340,191);scenario.Width=280;scenario.DropDownStyle=ComboBoxStyle.DropDownList;scenario.Items.AddRange(new object[]{"APPROVED","DECLINED","NO_RESPONSE"});scenario.SelectedIndex=0;Controls.Add(scenario);
            printerFailure.Text="Simulate printer failure";printerFailure.Location=new Point(30,244);printerFailure.Width=260;Controls.Add(printerFailure);
            ButtonAt(start,"1. Start purchase",30,293,200);ButtonAt(pay,"2. Pay (mock)",250,293,190);ButtonAt(expire,"QR expired",460,293,165);ButtonAt(retry,"Retry once",645,293,165);
            ButtonAt(late,"Reconcile: late success",30,355,285);
            paymentDetails.Location=new Point(30,410);paymentDetails.Size=new Size(850,60);paymentDetails.ForeColor=Color.DarkSlateGray;Controls.Add(paymentDetails);
            status.Location=new Point(30,480);status.Size=new Size(850,130);status.Font=new Font("Consolas",10);status.Text="Connect MSSQL and run the scripts first.";Controls.Add(status);
            start.Click+=(s,e)=>Execute(()=>{if(routes.SelectedItem==null)throw new InvalidOperationException("Select route");sessionId=controller.StartPurchase((Route)routes.SelectedItem);retryCount=0;transactionId=Guid.Empty;SetStatus("Purchase started: "+sessionId);});
            pay.Click+=(s,e)=>Execute(()=>{if(sessionId==Guid.Empty)throw new InvalidOperationException("Press Start first");if(transactionId==Guid.Empty){var t=controller.ChoosePayment(sessionId,(string)method.SelectedItem,(string)method.SelectedItem=="QR"?"VNPAY_MOCK":"CARD_GATEWAY");transactionId=t.TransactionId;}var r=controller.SubmitMockPayment(transactionId,(string)scenario.SelectedItem,true,printerFailure.Checked);ShowResult(r);});
            expire.Click+=(s,e)=>Execute(()=>{if(transactionId==Guid.Empty)throw new InvalidOperationException("Start a QR payment first");ShowResult(controller.ExpireQr(transactionId));});
            retry.Click+=(s,e)=>Execute(()=>{if(transactionId==Guid.Empty)throw new InvalidOperationException("No payment to retry");var prev=repo.GetPayment(transactionId);if(prev.Status!=PaymentStatus.Failed && prev.Status!=PaymentStatus.Expired)throw new InvalidOperationException("Retry only after FAILED/EXPIRED");if(retryCount>=1)throw new InvalidOperationException("Only one retry per session");retryCount++;transactionId=Guid.Empty;SetStatus("Retry enabled. Select payment method, then Pay again.");});
            late.Click+=(s,e)=>Execute(()=>{if(transactionId==Guid.Empty)throw new InvalidOperationException("No payment");ShowResult(processor.ConfirmLateMock(transactionId));});
            Shown+=(s,e)=>Execute(InitializeBackend);
        }
        private TicketingRepository repo;
        private void InitializeBackend()
        {
            repo=new TicketingRepository();
            var gateway=new GatewayAdapter();var printer=new PrinterService();
            processor=new PaymentProcessor(repo,gateway,printer);controller=new TVMController(repo,processor);
            routes.Items.Clear();foreach(var r in controller.BrowseRoutes())routes.Items.Add(r);
            if(routes.Items.Count>0)routes.SelectedIndex=0;
            SetStatus("Connected to TVM_Ticketing. Select route and start purchase.");
        }
        private void AddLabel(string text,int x,int y){Controls.Add(new Label{Text=text,Location=new Point(x,y),AutoSize=true,Font=new Font("Segoe UI",10,FontStyle.Bold)});}
        private void ButtonAt(Button b,string text,int x,int y,int width){b.Text=text;b.Location=new Point(x,y);b.Size=new Size(width,42);b.BackColor=Color.AliceBlue;b.FlatStyle=FlatStyle.Flat;Controls.Add(b);}
        private void SetStatus(string text){status.Text=text;}
        private void ShowResult(PaymentResult r)
        {
            var extra=r.Ticket==null?"":"\nTicket ID: "+r.Ticket.TicketId+"\nBarcode: "+r.Ticket.Barcode;
            paymentDetails.Text="Session: "+sessionId+"\nPayment: "+r.TransactionId;
            SetStatus("STATUS: "+r.Status+"\n"+r.Message+"\nSupport reference: "+(r.SupportReference??"-")+extra);
        }
        private void Execute(Action action){try{action();}catch(Exception ex){MessageBox.Show(ex.Message,"TVM demo",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
    }
}
