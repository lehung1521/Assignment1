USE TVM_Ticketing;
GO
SET NOCOUNT ON;
DECLARE @origin UNIQUEIDENTIFIER, @a UNIQUEIDENTIFIER, @b UNIQUEIDENTIFIER, @c UNIQUEIDENTIFIER;
SELECT @origin=StationId FROM dbo.Station WHERE Code=N'ORIGIN';
IF @origin IS NULL BEGIN SET @origin=NEWID(); INSERT dbo.Station(StationId,Code,NameVi,NameEn) VALUES (@origin,N'ORIGIN',N'Bến Trung Tâm',N'Central Station'); END;
SELECT @a=StationId FROM dbo.Station WHERE Code=N'ST01';
IF @a IS NULL BEGIN SET @a=NEWID();INSERT dbo.Station(StationId,Code,NameVi,NameEn) VALUES(@a,N'ST01',N'Bến Thành',N'Ben Thanh');END;
SELECT @b=StationId FROM dbo.Station WHERE Code=N'ST02';
IF @b IS NULL BEGIN SET @b=NEWID();INSERT dbo.Station(StationId,Code,NameVi,NameEn) VALUES(@b,N'ST02',N'Thảo Điền',N'Thao Dien');END;
SELECT @c=StationId FROM dbo.Station WHERE Code=N'ST03';
IF @c IS NULL BEGIN SET @c=NEWID();INSERT dbo.Station(StationId,Code,NameVi,NameEn) VALUES(@c,N'ST03',N'Suối Tiên',N'Suoi Tien');END;
IF NOT EXISTS(SELECT 1 FROM dbo.Tvm WHERE Code=N'TVM-DEMO-01')
 INSERT dbo.Tvm(Code,Name,OriginStationId) VALUES(N'TVM-DEMO-01',N'TVM Demo – Central',@origin);
IF NOT EXISTS(SELECT 1 FROM dbo.Route WHERE Mode=N'BUS' AND LineCode=N'BUS01' AND OriginStationId=@origin AND DestinationStationId=@a)
 INSERT dbo.Route(Mode,LineCode,RouteNameVi,RouteNameEn,OriginStationId,DestinationStationId) VALUES(N'BUS',N'BUS01',N'Xe buýt Trung Tâm – Bến Thành',N'Bus Central – Ben Thanh',@origin,@a);
IF NOT EXISTS(SELECT 1 FROM dbo.Route WHERE Mode=N'MRT' AND LineCode=N'MRT01' AND OriginStationId=@origin AND DestinationStationId=@b)
 INSERT dbo.Route(Mode,LineCode,RouteNameVi,RouteNameEn,OriginStationId,DestinationStationId) VALUES(N'MRT',N'MRT01',N'Metro Trung Tâm – Thảo Điền',N'Metro Central – Thao Dien',@origin,@b);
IF NOT EXISTS(SELECT 1 FROM dbo.Route WHERE Mode=N'MRT' AND LineCode=N'MRT01' AND OriginStationId=@origin AND DestinationStationId=@c)
 INSERT dbo.Route(Mode,LineCode,RouteNameVi,RouteNameEn,OriginStationId,DestinationStationId) VALUES(N'MRT',N'MRT01',N'Metro Trung Tâm – Suối Tiên',N'Metro Central – Suoi Tien',@origin,@c);
INSERT dbo.FareRule(RouteId,PriceVnd)
SELECT r.RouteId,CASE WHEN r.Mode=N'BUS' THEN 7000 WHEN r.DestinationStationId=@b THEN 12000 ELSE 20000 END
FROM dbo.Route r WHERE r.OriginStationId=@origin AND NOT EXISTS(SELECT 1 FROM dbo.FareRule f WHERE f.RouteId=r.RouteId AND f.IsActive=1);
GO
