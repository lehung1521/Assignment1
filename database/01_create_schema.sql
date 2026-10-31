/* =====================================================================
   TVM Ticketing - MSSQL schema (SQL Server 2019+)
   Source of truth: Requirements (FR/NFR/DR), UC01-UC12, Class Diagram,
   State Chart, Backend_Integration_Contract.docx
   Run order: 01_create_schema.sql -> 02_seed_data.sql -> 03_procedures.sql
   sqlcmd users: always pass -I (QUOTED_IDENTIFIER ON) - filtered indexes
   and computed columns need it.
   NEVER store card number, PIN or CVV (NFR-04) - there is no column for it.
   ===================================================================== */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF DB_ID(N'TVM_Ticketing') IS NULL
    CREATE DATABASE TVM_Ticketing;
GO

USE TVM_Ticketing;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---------------------------------------------------------------------
   1. Catalogue: stations, TVM, routes, fares  (UC02, UC03, FR-03/07/08/23)
   --------------------------------------------------------------------- */
CREATE TABLE dbo.Station (
    StationId   UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Station_Id DEFAULT NEWID(),
    Code        NVARCHAR(20)     NOT NULL,
    NameVi      NVARCHAR(100)    NOT NULL,
    NameEn      NVARCHAR(100)    NOT NULL,
    IsActive    BIT              NOT NULL CONSTRAINT DF_Station_Active DEFAULT 1,
    CONSTRAINT PK_Station PRIMARY KEY (StationId),
    CONSTRAINT UQ_Station_Code UNIQUE (Code)
);
GO

CREATE TABLE dbo.Tvm (
    TvmId              UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Tvm_Id DEFAULT NEWID(),
    Code               NVARCHAR(20)     NOT NULL,
    Name               NVARCHAR(100)    NOT NULL,
    OriginStationId    UNIQUEIDENTIFIER NOT NULL,           -- FR-03: fixed origin per TVM
    Status             NVARCHAR(25)     NOT NULL CONSTRAINT DF_Tvm_Status DEFAULT N'IN_SERVICE',
    SalesBlocked       BIT              NOT NULL CONSTRAINT DF_Tvm_Blocked DEFAULT 0,
    BlockReason        NVARCHAR(200)    NULL,                -- e.g. PRINTER_FAILURE (UC11), NETWORK (UC12)
    PaperLevelPercent  TINYINT          NOT NULL CONSTRAINT DF_Tvm_Paper DEFAULT 100,
    DefaultLanguage    CHAR(2)          NOT NULL CONSTRAINT DF_Tvm_Lang DEFAULT 'vi',
    UpdatedAt          DATETIME2(3)     NOT NULL CONSTRAINT DF_Tvm_Upd DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_Tvm PRIMARY KEY (TvmId),
    CONSTRAINT UQ_Tvm_Code UNIQUE (Code),
    CONSTRAINT FK_Tvm_Station FOREIGN KEY (OriginStationId) REFERENCES dbo.Station (StationId),
    CONSTRAINT CK_Tvm_Status CHECK (Status IN (N'IN_SERVICE', N'OUT_OF_SERVICE', N'PAYMENT_UNAVAILABLE')),
    CONSTRAINT CK_Tvm_Paper CHECK (PaperLevelPercent BETWEEN 0 AND 100),
    CONSTRAINT CK_Tvm_Lang CHECK (DefaultLanguage IN ('vi', 'en'))
);
GO

CREATE TABLE dbo.Route (
    RouteId            UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Route_Id DEFAULT NEWID(),
    Mode               NVARCHAR(10)     NOT NULL,            -- DR-01: BUS or MRT only
    LineCode           NVARCHAR(20)     NOT NULL,            -- UC02: destinations grouped by line
    RouteNameVi        NVARCHAR(150)    NOT NULL,
    RouteNameEn        NVARCHAR(150)    NOT NULL,
    OriginStationId    UNIQUEIDENTIFIER NOT NULL,
    DestinationStationId UNIQUEIDENTIFIER NOT NULL,
    IsAvailable        BIT              NOT NULL CONSTRAINT DF_Route_Avail DEFAULT 1,   -- FR-08
    CONSTRAINT PK_Route PRIMARY KEY (RouteId),
    CONSTRAINT FK_Route_Origin FOREIGN KEY (OriginStationId) REFERENCES dbo.Station (StationId),
    CONSTRAINT FK_Route_Dest FOREIGN KEY (DestinationStationId) REFERENCES dbo.Station (StationId),
    CONSTRAINT CK_Route_Mode CHECK (Mode IN (N'BUS', N'MRT')),
    CONSTRAINT CK_Route_Diff CHECK (OriginStationId <> DestinationStationId),
    CONSTRAINT UQ_Route UNIQUE (Mode, LineCode, OriginStationId, DestinationStationId)
);
GO

CREATE TABLE dbo.FareRule (
    FareRuleId     UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Fare_Id DEFAULT NEWID(),
    RouteId        UNIQUEIDENTIFIER NOT NULL,
    PriceVnd       DECIMAL(18, 0)   NOT NULL,                -- VAT included (UC03)
    EffectiveFrom  DATETIME2(3)     NOT NULL CONSTRAINT DF_Fare_From DEFAULT SYSUTCDATETIME(),
    EffectiveTo    DATETIME2(3)     NULL,
    IsActive       BIT              NOT NULL CONSTRAINT DF_Fare_Active DEFAULT 1,
    UpdatedBy      NVARCHAR(100)    NULL,                    -- FR-23: central update by staff
    UpdatedAt      DATETIME2(3)     NOT NULL CONSTRAINT DF_Fare_Upd DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_FareRule PRIMARY KEY (FareRuleId),
    CONSTRAINT FK_Fare_Route FOREIGN KEY (RouteId) REFERENCES dbo.Route (RouteId),
    CONSTRAINT CK_Fare_Price CHECK (PriceVnd > 0),
    CONSTRAINT CK_Fare_Dates CHECK (EffectiveTo IS NULL OR EffectiveTo > EffectiveFrom)
);
GO
-- at most one active fare per route
CREATE UNIQUE INDEX UX_FareRule_OneActive ON dbo.FareRule (RouteId) WHERE IsActive = 1;
GO

/* ---------------------------------------------------------------------
   2. Purchase session (UC01)  - SessionId = one passenger purchase
   --------------------------------------------------------------------- */
CREATE TABLE dbo.PurchaseSession (
    SessionId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Session_Id DEFAULT NEWID(),
    TvmId              UNIQUEIDENTIFIER NOT NULL,
    RouteId            UNIQUEIDENTIFIER NULL,
    FareRuleId         UNIQUEIDENTIFIER NULL,
    FareVnd            DECIMAL(18, 0)   NULL,                -- fare snapshot taken at UC03
    Language           CHAR(2)          NOT NULL CONSTRAINT DF_Session_Lang DEFAULT 'vi',
    RetryCount         TINYINT          NOT NULL CONSTRAINT DF_Session_Retry DEFAULT 0,  -- one fresh attempt only
    Status             NVARCHAR(20)     NOT NULL CONSTRAINT DF_Session_Status DEFAULT N'ACTIVE',
    IsPassengerPresent BIT              NOT NULL CONSTRAINT DF_Session_Present DEFAULT 1,
    StartedAt          DATETIME2(3)     NOT NULL CONSTRAINT DF_Session_Start DEFAULT SYSUTCDATETIME(),
    LastActivityAt     DATETIME2(3)     NOT NULL CONSTRAINT DF_Session_Act DEFAULT SYSUTCDATETIME(),  -- 60 s idle watchdog
    EndedAt            DATETIME2(3)     NULL,
    EndReason          NVARCHAR(30)     NULL,                -- CANCELLED, IDLE_TIMEOUT, COMPLETED, ...
    CONSTRAINT PK_PurchaseSession PRIMARY KEY (SessionId),
    CONSTRAINT FK_Session_Tvm FOREIGN KEY (TvmId) REFERENCES dbo.Tvm (TvmId),
    CONSTRAINT FK_Session_Route FOREIGN KEY (RouteId) REFERENCES dbo.Route (RouteId),
    CONSTRAINT FK_Session_Fare FOREIGN KEY (FareRuleId) REFERENCES dbo.FareRule (FareRuleId),
    CONSTRAINT CK_Session_Retry CHECK (RetryCount BETWEEN 0 AND 1),
    CONSTRAINT CK_Session_Status CHECK (Status IN (N'ACTIVE', N'ENDED', N'COMPLETED', N'SUPPORT_REQUIRED')),
    CONSTRAINT CK_Session_Lang CHECK (Language IN ('vi', 'en')),
    CONSTRAINT CK_Session_Fare CHECK (FareVnd IS NULL OR FareVnd > 0)
);
GO
CREATE INDEX IX_Session_Tvm_Status ON dbo.PurchaseSession (TvmId, Status);
GO

/* ---------------------------------------------------------------------
   3. Payment transaction (UC04)  - TransactionId = one payment attempt
   --------------------------------------------------------------------- */
CREATE TABLE dbo.PurchaseTransaction (
    TransactionId       UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Tx_Id DEFAULT NEWID(),
    SessionId           UNIQUEIDENTIFIER NOT NULL,
    AttemptNo           TINYINT          NOT NULL,           -- 1, 2, ... within the session
    PaymentMethod       NVARCHAR(10)     NOT NULL,           -- CARD | QR
    AmountVnd           DECIMAL(18, 0)   NOT NULL,
    Status              NVARCHAR(20)     NOT NULL CONSTRAINT DF_Tx_Status DEFAULT N'CREATED',
    PaymentPhase AS (CASE Status
            WHEN N'CREATED'          THEN N'UNPAID'
            WHEN N'WAITING'          THEN N'UNPAID'
            WHEN N'PROCESSING'       THEN N'UNPAID'
            WHEN N'CONFIRMED'        THEN N'PAID'
            WHEN N'PENDING'          THEN N'PENDING_RECONCILIATION'
            WHEN N'CANCEL_PENDING'   THEN N'PENDING_RECONCILIATION'
            WHEN N'CANCELLED'        THEN N'CANCELLED'
            WHEN N'PAID_UNFULFILLED' THEN N'PAID_BUT_UNFULFILLED'
            WHEN N'COMPLETED'        THEN N'COMPLETED'
            WHEN N'FAILED'           THEN N'FAILED'
            WHEN N'EXPIRED'          THEN N'EXPIRED'
            ELSE N'UNKNOWN' END) PERSISTED,
    CancellationPending BIT              NOT NULL CONSTRAINT DF_Tx_CancelPend DEFAULT 0,
    SubmittedAt         DATETIME2(3)     NULL,               -- 30 s wait starts here, NOT when QR is shown
    ConfirmedAt         DATETIME2(3)     NULL,
    SupportReference    NVARCHAR(30)     NULL,               -- PENDING / PAID_UNFULFILLED (FR-18, FR-24)
    CreatedAt           DATETIME2(3)     NOT NULL CONSTRAINT DF_Tx_Created DEFAULT SYSUTCDATETIME(),
    UpdatedAt           DATETIME2(3)     NOT NULL CONSTRAINT DF_Tx_Upd DEFAULT SYSUTCDATETIME(),
    SyncedAt            DATETIME2(3)     NULL,               -- NULL = still buffered locally (UC12, NFR-14)
    RowVer              ROWVERSION       NOT NULL,           -- optimistic concurrency for the ORM
    CONSTRAINT PK_PurchaseTransaction PRIMARY KEY (TransactionId),
    CONSTRAINT FK_Tx_Session FOREIGN KEY (SessionId) REFERENCES dbo.PurchaseSession (SessionId),
    CONSTRAINT UQ_Tx_SessionAttempt UNIQUE (SessionId, AttemptNo),
    CONSTRAINT CK_Tx_Method CHECK (PaymentMethod IN (N'CARD', N'QR')),
    CONSTRAINT CK_Tx_Amount CHECK (AmountVnd > 0),
    CONSTRAINT CK_Tx_Attempt CHECK (AttemptNo >= 1),
    CONSTRAINT CK_Tx_Status CHECK (Status IN (N'CREATED', N'WAITING', N'PROCESSING', N'CONFIRMED', N'PENDING',
            N'CANCEL_PENDING', N'FAILED', N'EXPIRED', N'CANCELLED', N'PAID_UNFULFILLED', N'COMPLETED'))
);
GO
-- only ONE non-final transaction per session at any time
CREATE UNIQUE INDEX UX_Tx_OneOpenPerSession ON dbo.PurchaseTransaction (SessionId)
    WHERE Status <> N'FAILED' AND Status <> N'EXPIRED' AND Status <> N'CANCELLED'
      AND Status <> N'PAID_UNFULFILLED' AND Status <> N'COMPLETED';
-- unique support reference
CREATE UNIQUE INDEX UX_Tx_SupportRef ON dbo.PurchaseTransaction (SupportReference) WHERE SupportReference IS NOT NULL;
CREATE INDEX IX_Tx_Status ON dbo.PurchaseTransaction (Status, UpdatedAt);
CREATE INDEX IX_Tx_Unsynced ON dbo.PurchaseTransaction (CreatedAt) WHERE SyncedAt IS NULL;
GO

CREATE TABLE dbo.PaymentAttempt (
    AttemptId         UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Attempt_Id DEFAULT NEWID(),
    TransactionId     UNIQUEIDENTIFIER NOT NULL,             -- 1 : 1 with the transaction
    Provider          NVARCHAR(30)     NULL,                 -- CARD_GATEWAY, VIETQR, MOMO, VNPAY, ZALOPAY
    GatewayReference  NVARCHAR(100)    NULL,
    QrPayload         NVARCHAR(2000)   NULL,
    QrGeneratedAt     DATETIME2(3)     NULL,
    QrExpiresAt       DATETIME2(3)     NULL,                 -- generation + 120 s
    LastGatewayResult NVARCHAR(20)     NULL,
    LastInquiryAt     DATETIME2(3)     NULL,
    CreatedAt         DATETIME2(3)     NOT NULL CONSTRAINT DF_Attempt_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_PaymentAttempt PRIMARY KEY (AttemptId),
    CONSTRAINT UQ_Attempt_Tx UNIQUE (TransactionId),
    CONSTRAINT FK_Attempt_Tx FOREIGN KEY (TransactionId) REFERENCES dbo.PurchaseTransaction (TransactionId)
);
GO
CREATE UNIQUE INDEX UX_Attempt_GatewayRef ON dbo.PaymentAttempt (GatewayReference) WHERE GatewayReference IS NOT NULL;
GO

/* ---------------------------------------------------------------------
   4. Ticket (UC10, DR-02)  - at most ONE ticket per session
   --------------------------------------------------------------------- */
CREATE TABLE dbo.Ticket (
    TicketId       UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Ticket_Id DEFAULT NEWID(),
    SessionId      UNIQUEIDENTIFIER NOT NULL,
    TransactionId  UNIQUEIDENTIFIER NOT NULL,
    Barcode AS (CONVERT(NVARCHAR(36), TicketId)) PERSISTED,   -- barcode always encodes the ticket ID
    Status         NVARCHAR(15)     NOT NULL CONSTRAINT DF_Ticket_Status DEFAULT N'RESERVED',
    ReservedAt     DATETIME2(3)     NOT NULL CONSTRAINT DF_Ticket_Res DEFAULT SYSUTCDATETIME(),
    PrintedAt      DATETIME2(3)     NULL,
    IssuedAt       DATETIME2(3)     NULL,
    CONSTRAINT PK_Ticket PRIMARY KEY (TicketId),
    CONSTRAINT UQ_Ticket_Session UNIQUE (SessionId),          -- one purchase = one ticket
    CONSTRAINT UQ_Ticket_Tx UNIQUE (TransactionId),
    CONSTRAINT FK_Ticket_Session FOREIGN KEY (SessionId) REFERENCES dbo.PurchaseSession (SessionId),
    CONSTRAINT FK_Ticket_Tx FOREIGN KEY (TransactionId) REFERENCES dbo.PurchaseTransaction (TransactionId),
    CONSTRAINT CK_Ticket_Status CHECK (Status IN (N'RESERVED', N'ISSUED', N'PRINT_FAILED'))
);
GO
CREATE UNIQUE INDEX UX_Ticket_Barcode ON dbo.Ticket (Barcode);
GO

/* ---------------------------------------------------------------------
   5. State machine guard + audit trail  (State Chart)
   --------------------------------------------------------------------- */
CREATE TABLE dbo.TransactionStatusTransition (
    FromStatus  NVARCHAR(20)  NOT NULL,
    ToStatus    NVARCHAR(20)  NOT NULL,
    Description NVARCHAR(200) NULL,
    CONSTRAINT PK_StatusTransition PRIMARY KEY (FromStatus, ToStatus)
);
GO

INSERT INTO dbo.TransactionStatusTransition (FromStatus, ToStatus, Description) VALUES
 (N'CREATED',        N'WAITING',          N'QR created / card ready'),
 (N'CREATED',        N'CANCELLED',        N'cancel or Back before anything is sent'),
 (N'WAITING',        N'PROCESSING',       N'payment submitted / authorization sent'),
 (N'WAITING',        N'EXPIRED',          N'QR 120 s and no submitted payment'),
 (N'WAITING',        N'CANCELLED',        N'gateway void confirmed'),
 (N'WAITING',        N'CANCEL_PENDING',   N'void unclear'),
 (N'WAITING',        N'PENDING',          N'network lost / outcome unknown'),
 (N'PROCESSING',     N'CONFIRMED',        N'verified SUCCESS'),
 (N'PROCESSING',     N'FAILED',           N'verified DECLINED / FAILED'),
 (N'PROCESSING',     N'PENDING',          N'no final result within 30 s'),
 (N'PROCESSING',     N'CANCEL_PENDING',   N'void result unknown'),
 (N'CANCEL_PENDING', N'CANCELLED',        N'gateway confirms void'),
 (N'CANCEL_PENDING', N'CONFIRMED',        N'verified SUCCESS has priority over cancel'),
 (N'CANCEL_PENDING', N'PENDING',          N'status inquiry unresolved'),
 (N'PENDING',        N'CONFIRMED',        N'verified SUCCESS, passenger present'),
 (N'PENDING',        N'FAILED',           N'verified failure'),
 (N'PENDING',        N'CANCELLED',        N'verified void'),
 (N'PENDING',        N'PAID_UNFULFILLED', N'late verified SUCCESS, passenger absent'),
 (N'EXPIRED',        N'PAID_UNFULFILLED', N'late verified SUCCESS after QR expiry (exception)'),
 (N'CONFIRMED',      N'COMPLETED',        N'ticket printed exactly once'),
 (N'CONFIRMED',      N'PAID_UNFULFILLED', N'printing fails / passenger left / duplicate payment in session');
GO

CREATE TABLE dbo.TransactionStatusHistory (
    HistoryId     BIGINT IDENTITY(1, 1) NOT NULL,
    TransactionId UNIQUEIDENTIFIER NOT NULL,
    FromStatus    NVARCHAR(20)     NULL,
    ToStatus      NVARCHAR(20)     NOT NULL,
    Reason        NVARCHAR(100)    NULL,
    ChangedAt     DATETIME2(3)     NOT NULL CONSTRAINT DF_Hist_At DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_StatusHistory PRIMARY KEY (HistoryId),
    CONSTRAINT FK_Hist_Tx FOREIGN KEY (TransactionId) REFERENCES dbo.PurchaseTransaction (TransactionId)
);
GO
CREATE INDEX IX_Hist_Tx ON dbo.TransactionStatusHistory (TransactionId, HistoryId);
GO

CREATE TRIGGER dbo.trg_PurchaseTransaction_Status
ON dbo.PurchaseTransaction
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1
               FROM inserted i
               JOIN deleted d ON d.TransactionId = i.TransactionId
               WHERE i.Status <> d.Status
                 AND NOT EXISTS (SELECT 1 FROM dbo.TransactionStatusTransition t
                                 WHERE t.FromStatus = d.Status AND t.ToStatus = i.Status))
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 51001, N'Illegal payment status transition (see TransactionStatusTransition).', 1;
    END;

    IF EXISTS (SELECT 1 FROM inserted i
               WHERE NOT EXISTS (SELECT 1 FROM deleted d WHERE d.TransactionId = i.TransactionId)
                 AND i.Status <> N'CREATED')
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 51002, N'A new transaction must start in status CREATED.', 1;
    END;

    INSERT INTO dbo.TransactionStatusHistory (TransactionId, FromStatus, ToStatus, Reason)
    SELECT i.TransactionId, d.Status, i.Status,
           CONVERT(NVARCHAR(100), SESSION_CONTEXT(N'status_reason'))
    FROM inserted i
    LEFT JOIN deleted d ON d.TransactionId = i.TransactionId
    WHERE d.TransactionId IS NULL OR d.Status <> i.Status;
END;
GO

/* ---------------------------------------------------------------------
   6. Gateway callbacks, security, reconciliation, maintenance
   --------------------------------------------------------------------- */
CREATE TABLE dbo.GatewayCallbackLog (
    CallbackId      BIGINT IDENTITY(1, 1) NOT NULL,
    TransactionId   UNIQUEIDENTIFIER NULL,                   -- NULL when the id is unknown
    GatewayEventId  NVARCHAR(100)    NOT NULL,               -- idempotency key (NFR-05)
    Source          NVARCHAR(20)     NOT NULL,
    ResultCode      NVARCHAR(20)     NOT NULL,
    AmountVnd       DECIMAL(18, 0)   NULL,
    SignatureValid  BIT              NOT NULL,
    Outcome         NVARCHAR(30)     NOT NULL,
    ReceivedAt      DATETIME2(3)     NOT NULL CONSTRAINT DF_Cb_At DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_GatewayCallbackLog PRIMARY KEY (CallbackId),
    CONSTRAINT FK_Cb_Tx FOREIGN KEY (TransactionId) REFERENCES dbo.PurchaseTransaction (TransactionId),
    CONSTRAINT CK_Cb_Source CHECK (Source IN (N'CALLBACK', N'POLL', N'INQUIRY', N'AUTH_RESPONSE'))
);
GO
CREATE UNIQUE INDEX UX_Cb_Idempotency ON dbo.GatewayCallbackLog (TransactionId, GatewayEventId) WHERE TransactionId IS NOT NULL;
GO

CREATE TABLE dbo.SecurityEvent (
    EventId       BIGINT IDENTITY(1, 1) NOT NULL,
    TransactionId UNIQUEIDENTIFIER NULL,
    EventType     NVARCHAR(40)     NOT NULL,                 -- INVALID_SIGNATURE, AMOUNT_MISMATCH, UNKNOWN_TRANSACTION, ...
    Detail        NVARCHAR(500)    NULL,
    CreatedAt     DATETIME2(3)     NOT NULL CONSTRAINT DF_Sec_At DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_SecurityEvent PRIMARY KEY (EventId)
);
GO

CREATE TABLE dbo.ReconciliationTask (
    TaskId         BIGINT IDENTITY(1, 1) NOT NULL,
    TransactionId  UNIQUEIDENTIFIER NOT NULL,
    Reason         NVARCHAR(20)     NOT NULL,
    Status         NVARCHAR(10)     NOT NULL CONSTRAINT DF_Rec_Status DEFAULT N'OPEN',
    InquiryCount   INT              NOT NULL CONSTRAINT DF_Rec_Cnt DEFAULT 0,
    NextInquiryAt  DATETIME2(3)     NOT NULL CONSTRAINT DF_Rec_Next DEFAULT SYSUTCDATETIME(),
    LastInquiryAt  DATETIME2(3)     NULL,
    Note           NVARCHAR(300)    NULL,
    CreatedAt      DATETIME2(3)     NOT NULL CONSTRAINT DF_Rec_Created DEFAULT SYSUTCDATETIME(),
    ResolvedAt     DATETIME2(3)     NULL,
    CONSTRAINT PK_ReconciliationTask PRIMARY KEY (TaskId),
    CONSTRAINT FK_Rec_Tx FOREIGN KEY (TransactionId) REFERENCES dbo.PurchaseTransaction (TransactionId),
    CONSTRAINT CK_Rec_Reason CHECK (Reason IN (N'PENDING', N'CANCEL_PENDING', N'LATE_SUCCESS', N'CONFLICT', N'NETWORK_RECOVERY')),
    CONSTRAINT CK_Rec_Status CHECK (Status IN (N'OPEN', N'RESOLVED'))
);
GO
CREATE UNIQUE INDEX UX_Rec_OpenPerReason ON dbo.ReconciliationTask (TransactionId, Reason) WHERE Status = N'OPEN';
CREATE INDEX IX_Rec_Due ON dbo.ReconciliationTask (NextInquiryAt) WHERE Status = N'OPEN';
GO

CREATE TABLE dbo.MaintenanceAlert (
    AlertId        BIGINT IDENTITY(1, 1) NOT NULL,
    TvmId          UNIQUEIDENTIFIER NOT NULL,
    AlertType      NVARCHAR(25)     NOT NULL,
    Detail         NVARCHAR(300)    NULL,
    TransactionId  UNIQUEIDENTIFIER NULL,
    CreatedAt      DATETIME2(3)     NOT NULL CONSTRAINT DF_Alert_Created DEFAULT SYSUTCDATETIME(),
    SentAt         DATETIME2(3)     NULL,                    -- NULL = queued, send later (UC11 E1)
    ResolvedAt     DATETIME2(3)     NULL,
    CONSTRAINT PK_MaintenanceAlert PRIMARY KEY (AlertId),
    CONSTRAINT FK_Alert_Tvm FOREIGN KEY (TvmId) REFERENCES dbo.Tvm (TvmId),
    CONSTRAINT FK_Alert_Tx FOREIGN KEY (TransactionId) REFERENCES dbo.PurchaseTransaction (TransactionId),
    CONSTRAINT CK_Alert_Type CHECK (AlertType IN (N'PAPER_LOW', N'PRINTER_FAILURE', N'CARD_READER_FAILURE'))
);
GO

/* ---------------------------------------------------------------------
   7. Views for the backend
   --------------------------------------------------------------------- */
CREATE VIEW dbo.vw_AvailableRoutes
AS
SELECT r.RouteId, r.Mode, r.LineCode, r.RouteNameVi, r.RouteNameEn,
       o.StationId AS OriginStationId, o.NameVi AS OriginNameVi, o.NameEn AS OriginNameEn,
       d.StationId AS DestinationStationId, d.NameVi AS DestinationNameVi, d.NameEn AS DestinationNameEn,
       f.FareRuleId, f.PriceVnd
FROM dbo.Route r
JOIN dbo.Station o ON o.StationId = r.OriginStationId AND o.IsActive = 1
JOIN dbo.Station d ON d.StationId = r.DestinationStationId AND d.IsActive = 1
JOIN dbo.FareRule f ON f.RouteId = r.RouteId AND f.IsActive = 1
                   AND f.EffectiveFrom <= SYSUTCDATETIME()
                   AND (f.EffectiveTo IS NULL OR f.EffectiveTo > SYSUTCDATETIME())
WHERE r.IsAvailable = 1;
GO

CREATE VIEW dbo.vw_TicketPrintData
AS
SELECT t.TicketId, t.Barcode, t.Status AS TicketStatus, s.SessionId, tx.TransactionId,
       r.Mode, o.NameVi AS OriginNameVi, o.NameEn AS OriginNameEn,
       d.NameVi AS DestinationNameVi, d.NameEn AS DestinationNameEn,
       s.FareVnd, tx.ConfirmedAt AS PurchaseTime
FROM dbo.Ticket t
JOIN dbo.PurchaseSession s ON s.SessionId = t.SessionId
JOIN dbo.PurchaseTransaction tx ON tx.TransactionId = t.TransactionId
JOIN dbo.Route r ON r.RouteId = s.RouteId
JOIN dbo.Station o ON o.StationId = r.OriginStationId
JOIN dbo.Station d ON d.StationId = r.DestinationStationId;
GO
