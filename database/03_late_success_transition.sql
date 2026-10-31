-- Add direct verified-late-success transitions required by mock reconciliation.
-- Must run after 01_create_schema.sql (idempotent).
USE TVM_Ticketing;
GO
IF NOT EXISTS(SELECT 1 FROM dbo.TransactionStatusTransition WHERE FromStatus=N'PROCESSING' AND ToStatus=N'PAID_UNFULFILLED')
 INSERT dbo.TransactionStatusTransition(FromStatus,ToStatus,Description) VALUES(N'PROCESSING',N'PAID_UNFULFILLED',N'Verified SUCCESS after passenger leaves');
IF NOT EXISTS(SELECT 1 FROM dbo.TransactionStatusTransition WHERE FromStatus=N'CANCEL_PENDING' AND ToStatus=N'PAID_UNFULFILLED')
 INSERT dbo.TransactionStatusTransition(FromStatus,ToStatus,Description) VALUES(N'CANCEL_PENDING',N'PAID_UNFULFILLED',N'Verified SUCCESS after passenger leaves during void');
GO
