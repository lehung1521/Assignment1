using SmartTicketing.Data;
using SmartTicketing.Services;
var builder=WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options => { options.Cookie.HttpOnly=true; options.Cookie.IsEssential=true; options.IdleTimeout=TimeSpan.FromMinutes(30); options.Cookie.SameSite=SameSiteMode.Lax; });
builder.Services.AddScoped<TicketingRepository>(sp => new TicketingRepository(builder.Configuration.GetConnectionString("TVM")));
builder.Services.AddScoped<IPaymentGateway,GatewayAdapter>();
builder.Services.AddScoped<IPrinterDevice,PrinterService>();
builder.Services.AddScoped<PaymentProcessor>();
builder.Services.AddScoped<TVMController>();
var app=builder.Build();
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Ticket/Error"); app.UseHsts(); }
app.UseHttpsRedirection(); app.UseStaticFiles(); app.UseRouting(); app.UseSession();
app.MapControllerRoute(name:"default",pattern:"{controller=Ticket}/{action=Index}/{id?}");
app.Run();