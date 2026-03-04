using System ;
using System.Collections.Generic ;
using System.Linq ;
using System.Text ;
using Arent3d.Architecture.Routing ;
using Arent3d.Architecture.Routing.AppBase ;
using Arent3d.Architecture.Routing.AppBase.Commands.Routing ;
using Arent3d.Architecture.Routing.AppBase.Forms ;
using Arent3d.Architecture.Routing.EndPoints ;
using Arent3d.Architecture.Routing.StorableCaches ;
using Arent3d.Revit ;
using Arent3d.Revit.I18n ;
using Arent3d.Revit.UI ;
using Arent3d.Utility ;
using Autodesk.Revit.Attributes ;
using Autodesk.Revit.DB ;
using Autodesk.Revit.DB.Mechanical ;
using Autodesk.Revit.UI ;
using Autodesk.Revit.UI.Selection ;

namespace Arent3d.Architecture.Routing.Mechanical.App.Commands.Routing
{
  /// <summary>
  /// Adapter to use default RouteProperties as IRouteProperty (no dialog).
  /// </summary>
  internal sealed class DefaultRoutePropertyAdapter : IRouteProperty
  {
    private readonly RouteProperties _props ;

    public DefaultRoutePropertyAdapter( RouteProperties props ) => _props = props ;

    public MEPSystemType? GetSystemType() => _props.SystemType ;
    public MEPCurveType GetCurveType() => _props.CurveType ?? throw new InvalidOperationException( "CurveType is required." ) ;
    public double GetDiameter() => _props.Diameter ?? 0 ;
    public bool GetRouteOnPipeSpace() => _props.IsRouteOnPipeSpace ?? false ;
    public FixedHeight? GetFromFixedHeight() => _props.FromFixedHeight ;
    public FixedHeight? GetToFixedHeight() => _props.ToFixedHeight ;
    public AvoidType GetAvoidType() => _props.AvoidType ?? AvoidType.Whichever ;
    public Opening? GetShaft() => _props.Shaft ;
  }

  [Transaction( TransactionMode.Manual )]
  [DisplayNameKey( "Mechanical.App.Commands.Routing.AutoRouteSupplyAirGrillCommand", DefaultString = "Auto Routing\nSupply Air Grill" )]
  [Image( "resources/RerouteAll.png" )]
  public class AutoRouteSupplyAirGrillCommand : RoutingCommandBase<AutoRouteSupplyAirGrillCommand.AutoRouteState>
  {
    /// <summary>
    /// Pairs of (AIR DISTRIBUTION BOX Out Connector, Supply Air Grill Connector) to route.
    /// </summary>
    public record AutoRouteState(
      IReadOnlyList<(Connector FromOut, Connector ToGrill)> Pairs,
      IRouteProperty RouteProperty,
      MEPSystemClassificationInfo ClassificationInfo ) ;

    protected override string GetTransactionNameKey() => "TransactionName.Commands.Routing.AutoRouteSupplyAirGrill" ;

    protected override RoutingExecutor CreateRoutingExecutor( Document document, View view ) => AppCommandSettings.CreateRoutingExecutor( document, view ) ;

    protected override OperationResult<AutoRouteState> OperateUI( ExternalCommandData commandData, ElementSet elements )
    {
      var uiDocument = commandData.Application.ActiveUIDocument ;
      var document = uiDocument.Document ;

      if ( uiDocument.ActiveView is not ViewPlan )
      {
        TaskDialog.Show( "Auto Routing Supply Air Grill", "Dialog.Commands.Routing.AutoRouteSupplyAirGrill.Require2D".GetAppStringByKeyOrDefault( "This command only runs in 2D. Please switch to a Floor Plan or Ceiling Plan view." ) ) ;
        return OperationResult<AutoRouteState>.Cancelled ;
      }

      // Người dùng chỉ cần click vào AIR DISTRIBUTION BOX; không cần chọn connector cụ thể.
      var pickedRef = uiDocument.Selection.PickObject(
        ObjectType.Element,
        new AirDistributionBoxSelectionFilter(),
        "Pick the air distribution box for Auto Routing Supply Air Grill." ) ;
      if ( pickedRef == null )
        return OperationResult<AutoRouteState>.Cancelled ;

      var boxElement = document.GetElement( pickedRef.ElementId ) ;
      if ( boxElement?.GetConnectors() is not { } boxConnectors )
        return OperationResult<AutoRouteState>.Cancelled ;

      Connector? inConnector = null ;
      var outConnectorsList = new List<Connector>() ;

      foreach ( var c in boxConnectors )
      {
        if ( c.Domain != Domain.DomainHvac || c.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
        if ( ! c.IsAnyEnd() ) continue ;
        if ( c.Direction == FlowDirectionType.In || c.Direction == FlowDirectionType.Bidirectional )
          inConnector ??= c ;
        else if ( c.Direction == FlowDirectionType.Out )
          outConnectorsList.Add( c ) ;
      }

      if ( inConnector == null || outConnectorsList.Count == 0 )
      {
        TaskDialog.Show( "Auto Routing Supply Air Grill", "Air distribution box must have at least one Supply Air IN connector and one Supply Air OUT connector." ) ;
        return OperationResult<AutoRouteState>.Cancelled ;
      }

      var inOrigin = inConnector.Origin ;
      var fromLevelId = boxElement.GetLevelId() ;
      if ( ElementId.InvalidElementId == fromLevelId )
        fromLevelId = document.GuessLevel( inOrigin ).Id ;
      var viewPlan = (ViewPlan) uiDocument.ActiveView ;
      // Đường chia trên/dưới: đi qua tâm box, "trên" = hướng lên màn hình (UpDirection của view) để khớp đường đỏ người dùng kẻ.
      var boxCenter = GetBoxCenter( inOrigin, outConnectorsList ) ;
      var splitUp = new XYZ( viewPlan.UpDirection.X, viewPlan.UpDirection.Y, 0 ) ;
      if ( splitUp.GetLength() < 1e-6 ) splitUp = XYZ.BasisY ;
      else splitUp = splitUp.Normalize() ;
      var levelIdForFilter = viewPlan.GenLevel?.Id ?? fromLevelId ;

      // Dùng thông tin system/type/đường kính từ OUT connector đầu tiên làm chuẩn cho mọi route.
      var referenceOut = outConnectorsList[ 0 ] ;
      if ( MEPSystemClassificationInfo.From( referenceOut ) is not { } classificationInfo )
        return OperationResult<AutoRouteState>.Cancelled ;
      if ( RouteMEPSystem.GetSystemType( document, referenceOut ) is not { } defaultSystemType )
        return OperationResult<AutoRouteState>.Cancelled ;
      var defaultCurveType = RouteMEPSystem.GetMEPCurveType( document, new[] { referenceOut }, defaultSystemType ) ;
      var defaultDiameter = referenceOut.GetDiameter() ;

      var routeChoiceSpec = new RoutePropertyTypeList( document, classificationInfo, fromLevelId, fromLevelId ) ;
      var routeProperties = new RouteProperties( document, classificationInfo, defaultSystemType, defaultCurveType, routeChoiceSpec.StandardTypes?.FirstOrDefault(), defaultDiameter ) ;
      var routeProperty = new DefaultRoutePropertyAdapter( routeProperties ) ;

      // Thu thập đủ Supply Air Grill: theo level + elevation + khoảng Z; thêm theo view hiện tại (mọi grill thấy trên plan đều được bắt).
      var byLevel = CollectSupplyAirTerminalConnectors( document, levelIdForFilter, inOrigin, null ) ;
      var byElevation = CollectSupplyAirTerminalConnectorsByElevation( document, inOrigin, null ) ;
      var byZRange = CollectSupplyAirTerminalConnectorsByZRange( document, inOrigin ) ;
      // Nhiều category: MechanicalEquipment, GenericModel, DuctFitting (grill có thể thuộc bất kỳ), loại trừ box.
      var byZRangeMultiCat = CollectSupplyAirConnectorsByZRangeFromCategories( document, inOrigin, 100.0, boxElement.Id, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_DuctFitting ) ;
      // Bắt mọi phần tử có connector Supply Air trong khoảng Z (không lọc category) để không bỏ sót.
      var byZRangeAny = CollectSupplyAirConnectorsByZRangeFromAllWithConnectors( document, inOrigin, 100.0, boxElement.Id ) ;
      // Thu thập theo view hiện tại.
      var byView = CollectSupplyAirConnectorsInView( document, viewPlan.Id, inOrigin, boxElement.Id ) ;
      // Thu thập từ MỌI ViewPlan cùng level (vd. HVAC-Floor Plans- 1 Mech) — view template có thể ẩn phần tử ở view khác.
      var byAllPlansOnLevel = CollectSupplyAirConnectorsFromAllPlanViewsOnLevel( document, levelIdForFilter, inOrigin, boxElement.Id ) ;
      // Thu theo khoảng Z của level (Level.Elevation ±) — mở rộng xuống dưới level để bắt grill phía dưới.
      var planLevel = viewPlan.GenLevel ?? document.GetElement( fromLevelId ) as Level ;
      var byLevelElevationRange = planLevel != null ? CollectSupplyAirConnectorsByLevelElevationRange( document, planLevel, inOrigin, boxElement.Id ) : Array.Empty<Connector>() ;
      // Thu theo khoảng cách 3D từ In Connector — bán kính 5000 ft để bắt mọi grill trong vùng.
      var byDistance3D = CollectSupplyAirConnectorsByMaxDistance( document, inOrigin, 5000.0, boxElement.Id ) ;
      // Thu mọi grill trên cùng level trong bán kính — không phụ thuộc view.
      var byLevelWithinDistance = CollectSupplyAirConnectorsOnLevelWithinDistance( document, levelIdForFilter, inOrigin, 5000.0, boxElement.Id ) ;
      // Thu thêm theo vùng bounding box rất rộng quanh box (5000 ft mỗi chiều).
      var byBoundingBox = CollectSupplyAirConnectorsInBoundingBox( document, inOrigin, 5000.0, boxElement.Id ) ;
      // Thu thập từ MỌI phần tử có ConnectorManager trong bán kính (có box filter).
      var byAllWithConnectors = CollectSupplyAirConnectorsFromAllElementsWithConnectors( document, inOrigin, 5000.0, boxElement.Id ) ;
      // Thu thập từ TOÀN BỘ document (không dùng box) rồi lọc theo khoảng cách — bắt mọi điểm dù ngoài vùng.
      var byEntireDocument = CollectSupplyAirConnectorsFromEntireDocument( document, inOrigin, 5000.0, boxElement.Id ) ;
      // Merge theo (Owner.Id, Connector.Id) để không gộp nhầm hai connector khác nhau trên cùng phần tử; tránh sót điểm.
      var grillConnectorsMerged = MergeMultipleConnectorListsByConnectorId( document, byLevel, byElevation, byZRange, byZRangeMultiCat, byZRangeAny, byView, byAllPlansOnLevel, byLevelElevationRange, byDistance3D, byLevelWithinDistance, byBoundingBox, byAllWithConnectors, byEntireDocument ) ;
      var grillConnectorsHvac = grillConnectorsMerged.Where( c => c.Domain == Domain.DomainHvac ).ToList() ;
      // Ưu tiên grill nằm trong view hiện tại (byView hoặc byAllPlansOnLevel) để đủ 6 điểm trên plan; tránh 2 điểm “thừa” từ collector khác gây lỗi/thiếu nối.
      var inViewIds = new HashSet<(ElementId OwnerId, int ConnectorId)>( byView.Concat( byAllPlansOnLevel ).Select( c => ( c.Owner.Id, c.Id ) ) ) ;
      var grillConnectors = inViewIds.Count > 0
        ? grillConnectorsHvac.Where( c => inViewIds.Contains( ( c.Owner.Id, c.Id ) ) ).ToList()
        : grillConnectorsHvac ;

      // Chỉ dùng các grill có system khác Undefined cho auto-route (grill Undefined bỏ qua, không thông báo).
      grillConnectors = grillConnectors.Where( c => c.DuctSystemType != DuctSystemType.UndefinedSystemType ).ToList() ;

      if ( grillConnectors.Count == 0 )
      {
        TaskDialog.Show( "Auto Routing Supply Air Grill", "No supply air terminals (grilles) found on this level. Ensure there are Duct Terminals with Supply Air connectors on the current plan." ) ;
        return OperationResult<AutoRouteState>.Cancelled ;
      }

      var pairs = BuildOutToGrillPairs( boxCenter, splitUp, outConnectorsList, grillConnectors ).ToList() ;
      var tol = Math.Max( document.Application.VertexTolerance, 0.1 ) ;
      bool SameConnector( Connector? a, Connector? b )
      {
        if ( a == null || b == null ) return false ;
        if ( a.Owner.Id == b.Owner.Id && a.Id == b.Id ) return true ;
        return a.Owner.Id == b.Owner.Id && a.Origin.DistanceTo( b.Origin ) < tol ;
      }
      // Grill “đã ghép” chỉ so sánh (Owner.Id, Connector.Id), không dùng trùng Origin — tránh coi hai connector khác Id trên cùng phần tử là một, bỏ sót điểm.
      bool IsGrillAlreadyPaired( Connector grill, IReadOnlyList<(Connector FromOut, Connector ToGrill)> currentPairs )
      {
        foreach ( var (_, toGrill) in currentPairs )
        {
          if ( toGrill.Owner.Id == grill.Owner.Id && toGrill.Id == grill.Id ) return true ;
        }
        return false ;
      }
      // Đếm số cặp theo từng Out (theo chỉ số trong outConnectorsList) để không phụ thuộc reference.
      var outPairCounts = new int[ outConnectorsList.Count ] ;
      void IncrementOutPairCount( Connector? outConn )
      {
        if ( outConn == null ) return ;
        var outs = outConnectorsList! ;
        for ( var i = 0 ; i < outs.Count ; i++ )
        {
          var o = outs[ i ] ;
          if ( o != null && SameConnector( o, outConn! ) ) { outPairCounts[ i ]++ ; return ; }
        }
      }
      foreach ( var (fromOut, _) in pairs )
        IncrementOutPairCount( fromOut ) ;

      // Mọi grill chưa ghép đều được gán đúng một Out (ưu tiên Out ít cặp nhất, rồi gần nhất) — không bỏ sót.
      foreach ( var grill in grillConnectors )
      {
        if ( IsGrillAlreadyPaired( grill, pairs ) ) continue ;
        var best = outConnectorsList
          .Select( ( o, i ) => (Out: o, Index: i) )
          .OrderBy( t => outPairCounts[ t.Index ] )
          .ThenBy( t => t.Out.Origin.DistanceTo( grill.Origin ) )
          .FirstOrDefault() ;
        if ( best.Out == null ) continue ;
        var bestOut = best.Out ;
        pairs.Add( ( bestOut, grill ) ) ;
        IncrementOutPairCount( bestOut ) ;
      }

      if ( pairs.Count == 0 )
      {
        TaskDialog.Show( "Auto Routing Supply Air Grill", "No valid (Out, Grill) pairs after splitting by level and duct system." ) ;
        return OperationResult<AutoRouteState>.Cancelled ;
      }

      // Grill có System=UndefinedSystemType đưa lên đầu — engine thường từ chối khi To connector undefined nếu chạy sau; chạy trước (Out chưa nối ống) dễ tạo được.
      // Sau đó sắp theo khoảng cách (gần trước).
      pairs = pairs
        .OrderBy( p => boxCenter.DistanceTo( p.ToGrill.Origin ) )
        .ToList() ;
      return new OperationResult<AutoRouteState>( new AutoRouteState( pairs, routeProperty, classificationInfo ) ) ;
    }

    /// <summary>Tâm box (trung bình In + tất cả Out) làm gốc đường chia trên/dưới.</summary>
    private static XYZ GetBoxCenter( XYZ inOrigin, IReadOnlyList<Connector> outConnectors )
    {
      if ( outConnectors == null || outConnectors.Count == 0 ) return inOrigin ;
      var sum = inOrigin ;
      foreach ( var c in outConnectors )
        sum = sum.Add( c.Origin ) ;
      var n = 1 + outConnectors.Count ;
      return new XYZ( sum.X / n, sum.Y / n, sum.Z / n ) ;
    }

    /// <summary>
    /// Gộp hai danh sách connector, bỏ trùng (cùng element và cùng Origin) để không bỏ sót grill khi level ID và elevation không khớp hết.
    /// </summary>
    private static IReadOnlyList<Connector> MergeConnectorLists( IReadOnlyList<Connector> byLevel, IReadOnlyList<Connector> byElevation )
    {
      if ( byElevation.Count == 0 ) return byLevel ;
      if ( byLevel.Count == 0 ) return byElevation ;
      var result = new List<Connector>( byLevel ) ;
      var doc = byLevel[ 0 ].Owner.Document ;
      var tol = doc.Application.VertexTolerance ;
      foreach ( var c in byElevation )
      {
        if ( result.Any( r => r.Owner.Id == c.Owner.Id && r.Origin.DistanceTo( c.Origin ) < tol ) ) continue ;
        result.Add( c ) ;
      }
      return result ;
    }

    /// <summary>
    /// Gộp nhiều danh sách connector, bỏ trùng theo (Owner.Id, Origin). Tránh sót khi merge nhiều nguồn.
    /// </summary>
    private static IReadOnlyList<Connector> MergeMultipleConnectorLists( Document document, params IReadOnlyList<Connector>[] lists )
    {
      return MergeMultipleConnectorListsWithTolerance( document, document.Application.VertexTolerance, lists ) ;
    }

    /// <summary>
    /// Gộp nhiều danh sách connector với tolerance chỉ định (feet). Tolerance ≥ 0.1 giúp gộp đúng cùng một grill từ nhiều nguồn.
    /// </summary>
    private static IReadOnlyList<Connector> MergeMultipleConnectorListsWithTolerance( Document document, double toleranceFeet, params IReadOnlyList<Connector>[] lists )
    {
      if ( lists.Length == 0 ) return Array.Empty<Connector>() ;
      var result = new List<Connector>() ;
      foreach ( var list in lists )
      {
        if ( list == null || list.Count == 0 ) continue ;
        foreach ( var c in list )
        {
          if ( result.Any( r => r.Owner.Id == c.Owner.Id && r.Origin.DistanceTo( c.Origin ) < toleranceFeet ) ) continue ;
          result.Add( c ) ;
        }
      }
      return result ;
    }

    /// <summary>
    /// Gộp nhiều danh sách connector, bỏ trùng theo (Owner.Id, Connector.Id). Không gộp nhầm hai connector khác nhau trên cùng phần tử.
    /// </summary>
    private static IReadOnlyList<Connector> MergeMultipleConnectorListsByConnectorId( Document document, params IReadOnlyList<Connector>[] lists )
    {
      if ( lists.Length == 0 ) return Array.Empty<Connector>() ;
      var result = new List<Connector>() ;
      foreach ( var list in lists )
      {
        if ( list == null || list.Count == 0 ) continue ;
        foreach ( var c in list )
        {
          if ( result.Any( r => r.Owner.Id == c.Owner.Id && r.Id == c.Id ) ) continue ;
          result.Add( c ) ;
        }
      }
      return result ;
    }

    /// <summary>
    /// Góc (độ lớn, trong [0, PI]) tạo bởi vector basisZ của In Connector với vector đi từ In Connector tới point.
    /// Dùng để sắp xếp Out Connectors và Supply Air Grill theo thứ tự góc với basisZ (tổng quát cho mọi hướng box).
    /// </summary>
    private static double GetAngleWithBasisZ( XYZ inOrigin, XYZ basisZ, XYZ point )
    {
      var v = point - inOrigin ;
      var len = v.GetLength() ;
      if ( len < 1e-9 ) return 0 ;
      v = v.Multiply( 1.0 / len ) ;
      var dot = v.DotProduct( basisZ ) ;
      var clamped = Math.Max( -1, Math.Min( 1, dot ) ) ;
      return Math.Acos( clamped ) ;
    }

    /// <summary>
    /// Chia AIR DISTRIBUTION BOX thành 2 nửa trên/dưới: đường chia đi qua splitOrigin, "trên" = cùng chiều splitUp (hướng lên màn hình).
    /// Nối Out trên với Grill trên, Out dưới với Grill dưới; sắp xếp theo góc với trục ngang rồi ghép 1-1.
    /// </summary>
    private static IReadOnlyList<(Connector FromOut, Connector ToGrill)> BuildOutToGrillPairs(
      XYZ splitOrigin,
      XYZ splitUp,
      IReadOnlyList<Connector> outConnectors,
      IReadOnlyList<Connector> grillConnectors )
    {
      double DotUp( XYZ p ) => ( p - splitOrigin ).DotProduct( splitUp ) ;
      var horizontalDir = new XYZ( -splitUp.Y, splitUp.X, 0 ) ;
      if ( horizontalDir.GetLength() < 1e-6 ) horizontalDir = XYZ.BasisX ;
      else horizontalDir = horizontalDir.Normalize() ;
      double AngleWithHorizontal( Connector c )
      {
        var v = c.Origin - splitOrigin ;
        var len = v.GetLength() ;
        if ( len < 1e-9 ) return 0 ;
        v = v.Multiply( 1.0 / len ) ;
        var dot = Math.Max( -1, Math.Min( 1, v.DotProduct( horizontalDir ) ) ) ;
        return Math.Acos( dot ) ;
      }
      double DistSq( Connector c ) => splitOrigin.DistanceTo( c.Origin ) * splitOrigin.DistanceTo( c.Origin ) ;

      var outUpper = outConnectors.Where( c => DotUp( c.Origin ) >= 0 ).OrderBy( AngleWithHorizontal ).ThenBy( DistSq ).ToList() ;
      var outLower = outConnectors.Where( c => DotUp( c.Origin ) < 0 ).OrderBy( AngleWithHorizontal ).ThenBy( DistSq ).ToList() ;
      var grillUpper = grillConnectors.Where( c => DotUp( c.Origin ) >= 0 ).OrderBy( AngleWithHorizontal ).ThenBy( DistSq ).ToList() ;
      var grillLower = grillConnectors.Where( c => DotUp( c.Origin ) < 0 ).OrderBy( AngleWithHorizontal ).ThenBy( DistSq ).ToList() ;

      var pairs = new List<(Connector FromOut, Connector ToGrill)>() ;

      var nUpper = Math.Min( outUpper.Count, grillUpper.Count ) ;
      for ( var i = 0 ; i < nUpper ; i++ )
        pairs.Add( ( outUpper[ i ], grillUpper[ i ] ) ) ;
      var outUpperLeft = outUpper.Skip( nUpper ).ToList() ;
      var grillUpperLeft = grillUpper.Skip( nUpper ).ToList() ;

      var nLower = Math.Min( outLower.Count, grillLower.Count ) ;
      for ( var i = 0 ; i < nLower ; i++ )
        pairs.Add( ( outLower[ i ], grillLower[ i ] ) ) ;
      var outLowerLeft = outLower.Skip( nLower ).ToList() ;
      var grillLowerLeft = grillLower.Skip( nLower ).ToList() ;

      // Thừa: nửa trên thừa Grill + nửa dưới thừa Out → ghép ngược thứ tự.
      if ( grillUpperLeft.Count > 0 && outLowerLeft.Count > 0 )
      {
        var take = Math.Min( grillUpperLeft.Count, outLowerLeft.Count ) ;
        for ( var i = 0 ; i < take ; i++ )
          pairs.Add( ( outLowerLeft[ outLowerLeft.Count - 1 - i ], grillUpperLeft[ grillUpperLeft.Count - 1 - i ] ) ) ;
      }

      // Thừa: nửa dưới thừa Grill + nửa trên thừa Out → ghép ngược thứ tự.
      if ( grillLowerLeft.Count > 0 && outUpperLeft.Count > 0 )
      {
        var take = Math.Min( grillLowerLeft.Count, outUpperLeft.Count ) ;
        for ( var i = 0 ; i < take ; i++ )
          pairs.Add( ( outUpperLeft[ outUpperLeft.Count - 1 - i ], grillLowerLeft[ grillLowerLeft.Count - 1 - i ] ) ) ;
      }

      return pairs ;
    }

    /// <summary>
    /// Collect Supply Air duct terminal connectors (grilles) on the given level.
    /// Only includes connectors with same Duct System type as classificationInfo (Supply Air).
    /// </summary>
    private static IReadOnlyList<Connector> CollectSupplyAirTerminalConnectors( Document document, ElementId levelId, XYZ fromOrigin, MEPSystemClassificationInfo? classificationInfo )
    {
      var list = new List<(Connector Connector, double DistanceSq)>() ;

      var terminals = new FilteredElementCollector( document )
        .OfCategory( BuiltInCategory.OST_DuctTerminal )
        .WhereElementIsNotElementType()
        .ToElements() ;

      var useLevelFilter = ElementId.InvalidElementId != levelId ;

      foreach ( var elem in terminals )
      {
        if ( useLevelFilter )
        {
          var elemLevelId = elem.GetLevelId() ;
          if ( elemLevelId != levelId ) continue ;
        }

        foreach ( var conn in elem.GetConnectors() )
        {
          if ( conn.Domain != Domain.DomainHvac ) continue ;
          if ( conn.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
          if ( classificationInfo != null && ! classificationInfo.IsCompatibleTo( conn ) ) continue ;
          // Không lọc IsAnyEnd() để không bỏ sót grill (một số connector có thể không đánh dấu end).
          var d = fromOrigin.DistanceTo( conn.Origin ) ;
          list.Add( ( conn, d * d ) ) ;
        }
      }

      return list.OrderBy( x => x.DistanceSq ).Select( x => x.Connector ).ToList() ;
    }

    /// <summary>
    /// Thu thập Supply Air terminal trên cùng tầng (theo elevation). Không lọc IsAnyEnd để không bỏ sót.
    /// </summary>
    private static IReadOnlyList<Connector> CollectSupplyAirTerminalConnectorsByElevation( Document document, XYZ fromOrigin, MEPSystemClassificationInfo? classificationInfo )
    {
      var fromLevel = document.GuessLevel( fromOrigin ) ;
      var list = new List<(Connector Connector, double DistanceSq)>() ;

      var terminals = new FilteredElementCollector( document )
        .OfCategory( BuiltInCategory.OST_DuctTerminal )
        .WhereElementIsNotElementType()
        .ToElements() ;

      foreach ( var elem in terminals )
      {
        foreach ( var conn in elem.GetConnectors() )
        {
          if ( conn.Domain != Domain.DomainHvac ) continue ;
          if ( conn.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
          if ( classificationInfo != null && ! classificationInfo.IsCompatibleTo( conn ) ) continue ;
          if ( document.GuessLevel( conn.Origin ).Id != fromLevel.Id ) continue ;
          var d = fromOrigin.DistanceTo( conn.Origin ) ;
          list.Add( ( conn, d * d ) ) ;
        }
      }

      return list.OrderBy( x => x.DistanceSq ).Select( x => x.Connector ).ToList() ;
    }

    /// <summary>
    /// Thu thập mọi Supply Air connector của DuctTerminal có Origin.Z gần với fromOrigin.Z (cùng tầng theo Z).
    /// Bắt grill nằm sát biên level hoặc level ID không khớp. Dùng khoảng Z rộng (100 ft) để không bỏ sót.
    /// </summary>
    private static IReadOnlyList<Connector> CollectSupplyAirTerminalConnectorsByZRange( Document document, XYZ fromOrigin, double zToleranceFeet = 100.0 )
    {
      var zMin = fromOrigin.Z - zToleranceFeet ;
      var zMax = fromOrigin.Z + zToleranceFeet ;
      var list = new List<(Connector Connector, double DistanceSq)>() ;

      var terminals = new FilteredElementCollector( document )
        .OfCategory( BuiltInCategory.OST_DuctTerminal )
        .WhereElementIsNotElementType()
        .ToElements() ;

      foreach ( var elem in terminals )
      {
        foreach ( var conn in elem.GetConnectors() )
        {
          if ( conn.Domain != Domain.DomainHvac ) continue ;
          if ( conn.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
          if ( conn.Origin.Z < zMin || conn.Origin.Z > zMax ) continue ;
          var d = fromOrigin.DistanceTo( conn.Origin ) ;
          list.Add( ( conn, d * d ) ) ;
        }
      }

      return list.OrderBy( x => x.DistanceSq ).Select( x => x.Connector ).ToList() ;
    }

    /// <summary>
    /// Thu thập Supply Air connector từ nhiều category theo khoảng Z. Loại trừ excludeElementId (Air Distribution Box).
    /// </summary>
    private static IReadOnlyList<Connector> CollectSupplyAirConnectorsByZRangeFromCategories( Document document, XYZ fromOrigin, double zToleranceFeet = 100.0, ElementId? excludeElementId = null, params BuiltInCategory[] categories )
    {
      var zMin = fromOrigin.Z - zToleranceFeet ;
      var zMax = fromOrigin.Z + zToleranceFeet ;
      var list = new List<(Connector Connector, double DistanceSq)>() ;

      foreach ( var category in categories )
      {
        var elements = new FilteredElementCollector( document )
          .OfCategory( category )
          .WhereElementIsNotElementType()
          .ToElements() ;

        foreach ( var elem in elements )
        {
          if ( excludeElementId != null && elem.Id == excludeElementId ) continue ;
          foreach ( var conn in elem.GetConnectors() )
          {
            if ( conn.Domain != Domain.DomainHvac ) continue ;
            if ( conn.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
            if ( conn.Origin.Z < zMin || conn.Origin.Z > zMax ) continue ;
            var d = fromOrigin.DistanceTo( conn.Origin ) ;
            list.Add( ( conn, d * d ) ) ;
          }
        }
      }

      return list.OrderBy( x => x.DistanceSq ).Select( x => x.Connector ).ToList() ;
    }

    /// <summary>
    /// Thu thập mọi Supply Air connector từ phần tử có ConnectorManager (FamilyInstance, v.v.) trong khoảng Z.
    /// Bắt grill thuộc category bất kỳ. Loại trừ excludeElementId (Air Distribution Box).
    /// </summary>
    private static IReadOnlyList<Connector> CollectSupplyAirConnectorsByZRangeFromAllWithConnectors( Document document, XYZ fromOrigin, double zToleranceFeet = 100.0, ElementId? excludeElementId = null )
    {
      var zMin = fromOrigin.Z - zToleranceFeet ;
      var zMax = fromOrigin.Z + zToleranceFeet ;
      var list = new List<(Connector Connector, double DistanceSq)>() ;

      var familyInstances = new FilteredElementCollector( document )
        .OfClass( typeof( FamilyInstance ) )
        .WhereElementIsNotElementType()
        .ToElements() ;

      foreach ( var elem in familyInstances )
      {
        if ( excludeElementId != null && elem.Id == excludeElementId ) continue ;
        foreach ( var conn in elem.GetConnectors() )
        {
          if ( conn.Domain != Domain.DomainHvac ) continue ;
          if ( conn.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
          if ( conn.Origin.Z < zMin || conn.Origin.Z > zMax ) continue ;
          var d = fromOrigin.DistanceTo( conn.Origin ) ;
          list.Add( ( conn, d * d ) ) ;
        }
      }

      return list.OrderBy( x => x.DistanceSq ).Select( x => x.Connector ).ToList() ;
    }

    /// <summary>
    /// Thu thập Supply Air connector có Z nằm trong khoảng elevation của level (Level.Elevation ± 50 ft).
    /// Mở rộng xuống dưới level để bắt grill phía dưới (ceiling below, v.v.).
    /// </summary>
    private static IReadOnlyList<Connector> CollectSupplyAirConnectorsByLevelElevationRange( Document document, Level level, XYZ fromOrigin, ElementId excludeElementId )
    {
      const double heightFeet = 50.0 ;
      var zMin = level.Elevation - heightFeet ;
      var zMax = level.Elevation + heightFeet ;
      var list = new List<(Connector Connector, double DistanceSq)>() ;
      // DuctTerminal + MechanicalEquipment + GenericModel + FamilyInstance để vừa đủ vừa nhanh.
      var categories = new[] { BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_GenericModel } ;
      foreach ( var cat in categories )
      {
        foreach ( var elem in new FilteredElementCollector( document ).OfCategory( cat ).WhereElementIsNotElementType().ToElements() )
        {
          if ( elem.Id == excludeElementId ) continue ;
          foreach ( var conn in elem.GetConnectors() )
          {
            if ( conn.Domain != Domain.DomainHvac ) continue ;
            if ( conn.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
            if ( conn.Origin.Z < zMin || conn.Origin.Z > zMax ) continue ;
            var d = fromOrigin.DistanceTo( conn.Origin ) ;
            list.Add( ( conn, d * d ) ) ;
          }
        }
      }
      foreach ( var elem in new FilteredElementCollector( document ).OfClass( typeof( FamilyInstance ) ).WhereElementIsNotElementType().ToElements() )
      {
        if ( elem.Id == excludeElementId ) continue ;
        foreach ( var conn in elem.GetConnectors() )
        {
          if ( conn.Domain != Domain.DomainHvac ) continue ;
          if ( conn.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
          if ( conn.Origin.Z < zMin || conn.Origin.Z > zMax ) continue ;
          var d = fromOrigin.DistanceTo( conn.Origin ) ;
          list.Add( ( conn, d * d ) ) ;
        }
      }
      return list.OrderBy( x => x.DistanceSq ).Select( x => x.Connector ).ToList() ;
    }

    /// <summary>
    /// Thu thập mọi Supply Air connector trong khoảng cách 3D maxDistanceFeet từ fromOrigin.
    /// Không phụ thuộc level/view — bắt grill phía dưới hoặc xa level nếu vẫn gần box.
    /// </summary>
    private static IReadOnlyList<Connector> CollectSupplyAirConnectorsByMaxDistance( Document document, XYZ fromOrigin, double maxDistanceFeet, ElementId excludeElementId )
    {
      var maxSq = maxDistanceFeet * maxDistanceFeet ;
      var list = new List<(Connector Connector, double DistanceSq)>() ;
      var categories = new[] { BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_DuctFitting } ;
      foreach ( var cat in categories )
      {
        foreach ( var elem in new FilteredElementCollector( document ).OfCategory( cat ).WhereElementIsNotElementType().ToElements() )
        {
          if ( elem.Id == excludeElementId ) continue ;
          foreach ( var conn in elem.GetConnectors() )
          {
            if ( conn.Domain != Domain.DomainHvac ) continue ;
            if ( conn.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
            var d = fromOrigin.DistanceTo( conn.Origin ) ;
            var dSq = d * d ;
            if ( dSq <= maxSq ) list.Add( ( conn, dSq ) ) ;
          }
        }
      }
      foreach ( var elem in new FilteredElementCollector( document ).OfClass( typeof( FamilyInstance ) ).WhereElementIsNotElementType().ToElements() )
      {
        if ( elem.Id == excludeElementId ) continue ;
        foreach ( var conn in elem.GetConnectors() )
        {
          if ( conn.Domain != Domain.DomainHvac ) continue ;
          if ( conn.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
          var d = fromOrigin.DistanceTo( conn.Origin ) ;
          var dSq = d * d ;
          if ( dSq <= maxSq ) list.Add( ( conn, dSq ) ) ;
        }
      }
      return list.OrderBy( x => x.DistanceSq ).Select( x => x.Connector ).ToList() ;
    }

    /// <summary>
    /// Thu thập mọi connector HVAC từ TOÀN BỘ document (không dùng view/box), lọc theo khoảng cách từ fromOrigin.
    /// Đảm bảo không bỏ sót điểm do bounding box hoặc view.
    /// </summary>
    private static IReadOnlyList<Connector> CollectSupplyAirConnectorsFromEntireDocument( Document document, XYZ fromOrigin, double maxDistanceFeet, ElementId excludeElementId )
    {
      var maxSq = maxDistanceFeet * maxDistanceFeet ;
      var list = new List<(Connector Connector, double DistanceSq)>() ;
      foreach ( var elem in new FilteredElementCollector( document ).OfClass( typeof( FamilyInstance ) ).WhereElementIsNotElementType().ToElements() )
      {
        if ( elem.Id == excludeElementId ) continue ;
        try
        {
          foreach ( var conn in elem.GetConnectors() )
          {
            if ( conn.Domain != Domain.DomainHvac ) continue ;
            var d = fromOrigin.DistanceTo( conn.Origin ) ;
            var dSq = d * d ;
            if ( dSq <= maxSq ) list.Add( ( conn, dSq ) ) ;
          }
        }
        catch
        {
          // Bỏ qua phần tử không có ConnectorManager.
        }
      }
      foreach ( var elem in new FilteredElementCollector( document ).OfClass( typeof( Duct ) ).WhereElementIsNotElementType().ToElements() )
      {
        if ( elem.Id == excludeElementId ) continue ;
        try
        {
          foreach ( var conn in elem.GetConnectors() )
          {
            if ( conn.Domain != Domain.DomainHvac ) continue ;
            var d = fromOrigin.DistanceTo( conn.Origin ) ;
            var dSq = d * d ;
            if ( dSq <= maxSq ) list.Add( ( conn, dSq ) ) ;
          }
        }
        catch
        {
          // Bỏ qua.
        }
      }
      return list.OrderBy( x => x.DistanceSq ).Select( x => x.Connector ).ToList() ;
    }

    /// <summary>
    /// Thu thập Supply Air connector từ MỌI phần tử có ConnectorManager (FamilyInstance, MEPCurve) trong bán kính — không lọc category.
    /// Dùng cho sample-data hoặc grill thuộc category đặc biệt / view MEC 1.
    /// </summary>
    private static IReadOnlyList<Connector> CollectSupplyAirConnectorsFromAllElementsWithConnectors( Document document, XYZ fromOrigin, double maxDistanceFeet, ElementId excludeElementId )
    {
      var maxSq = maxDistanceFeet * maxDistanceFeet ;
      var list = new List<(Connector Connector, double DistanceSq)>() ;
      var min = new XYZ( fromOrigin.X - maxDistanceFeet, fromOrigin.Y - maxDistanceFeet, fromOrigin.Z - maxDistanceFeet ) ;
      var max = new XYZ( fromOrigin.X + maxDistanceFeet, fromOrigin.Y + maxDistanceFeet, fromOrigin.Z + maxDistanceFeet ) ;
      var boxFilter = new BoundingBoxIntersectsFilter( new Outline( min, max ) ) ;

      foreach ( var elem in new FilteredElementCollector( document ).OfClass( typeof( FamilyInstance ) ).WhereElementIsNotElementType().WherePasses( boxFilter ).ToElements() )
      {
        if ( elem.Id == excludeElementId ) continue ;
        try
        {
          foreach ( var conn in elem.GetConnectors() )
          {
            if ( conn.Domain != Domain.DomainHvac ) continue ;
            // Thu mọi connector HVAC (kể cả chưa gán system) để bắt điểm khoanh đỏ.
            var d = fromOrigin.DistanceTo( conn.Origin ) ;
            var dSq = d * d ;
            if ( dSq <= maxSq ) list.Add( ( conn, dSq ) ) ;
          }
        }
        catch
        {
          // Một số FamilyInstance có thể không có ConnectorManager hợp lệ.
        }
      }
      foreach ( var elem in new FilteredElementCollector( document ).OfClass( typeof( Duct ) ).WhereElementIsNotElementType().WherePasses( boxFilter ).ToElements() )
      {
        if ( elem.Id == excludeElementId ) continue ;
        try
        {
          foreach ( var conn in elem.GetConnectors() )
          {
            if ( conn.Domain != Domain.DomainHvac ) continue ;
            // Thu mọi connector HVAC để bắt điểm khoanh đỏ.
            var d = fromOrigin.DistanceTo( conn.Origin ) ;
            var dSq = d * d ;
            if ( dSq <= maxSq ) list.Add( ( conn, dSq ) ) ;
          }
        }
        catch
        {
          // Bỏ qua nếu không đọc được connector.
        }
      }
      return list.OrderBy( x => x.DistanceSq ).Select( x => x.Connector ).ToList() ;
    }

    /// <summary>
    /// Thu thập mọi Supply Air connector từ phần tử có bounding box cắt vùng (center ± halfExtentFeet) theo X,Y,Z.
    /// Bắt grill trong vùng rộng quanh box, không phụ thuộc level hay view.
    /// </summary>
    private static IReadOnlyList<Connector> CollectSupplyAirConnectorsInBoundingBox( Document document, XYZ center, double halfExtentFeet, ElementId excludeElementId )
    {
      var min = new XYZ( center.X - halfExtentFeet, center.Y - halfExtentFeet, center.Z - halfExtentFeet ) ;
      var max = new XYZ( center.X + halfExtentFeet, center.Y + halfExtentFeet, center.Z + halfExtentFeet ) ;
      var outline = new Outline( min, max ) ;
      var boxFilter = new BoundingBoxIntersectsFilter( outline ) ;
      var list = new List<(Connector Connector, double DistanceSq)>() ;
      var categories = new[] { BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_DuctFitting } ;
      foreach ( var cat in categories )
      {
        var elements = new FilteredElementCollector( document ).OfCategory( cat ).WhereElementIsNotElementType().WherePasses( boxFilter ).ToElements() ;
        foreach ( var elem in elements )
        {
          if ( elem.Id == excludeElementId ) continue ;
          foreach ( var conn in elem.GetConnectors() )
          {
            if ( conn.Domain != Domain.DomainHvac ) continue ;
            if ( conn.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
            var d = center.DistanceTo( conn.Origin ) ;
            list.Add( ( conn, d * d ) ) ;
          }
        }
      }
      var familyInstances = new FilteredElementCollector( document ).OfClass( typeof( FamilyInstance ) ).WhereElementIsNotElementType().WherePasses( boxFilter ).ToElements() ;
      foreach ( var elem in familyInstances )
      {
        if ( elem.Id == excludeElementId ) continue ;
        foreach ( var conn in elem.GetConnectors() )
        {
          if ( conn.Domain != Domain.DomainHvac ) continue ;
          if ( conn.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
          var d = center.DistanceTo( conn.Origin ) ;
          list.Add( ( conn, d * d ) ) ;
        }
      }
      return list.OrderBy( x => x.DistanceSq ).Select( x => x.Connector ).ToList() ;
    }

    /// <summary>
    /// Thu thập mọi Supply Air connector trên cùng level (theo levelId) trong bán kính maxDistanceFeet từ fromOrigin.
    /// Không phụ thuộc view hay crop — bắt grill ở góc (vd. dưới-phải) dù ngoài vùng hiển thị.
    /// </summary>
    private static IReadOnlyList<Connector> CollectSupplyAirConnectorsOnLevelWithinDistance( Document document, ElementId levelId, XYZ fromOrigin, double maxDistanceFeet, ElementId excludeElementId )
    {
      if ( levelId == ElementId.InvalidElementId ) return Array.Empty<Connector>() ;
      var maxSq = maxDistanceFeet * maxDistanceFeet ;
      var list = new List<(Connector Connector, double DistanceSq)>() ;
      var categories = new[] { BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_DuctFitting } ;
      foreach ( var cat in categories )
      {
        foreach ( var elem in new FilteredElementCollector( document ).OfCategory( cat ).WhereElementIsNotElementType().ToElements() )
        {
          if ( elem.Id == excludeElementId ) continue ;
          var elemLevelId = elem.GetLevelId() ;
          if ( elemLevelId != ElementId.InvalidElementId && elemLevelId != levelId ) continue ;
          foreach ( var conn in elem.GetConnectors() )
          {
            if ( conn.Domain != Domain.DomainHvac ) continue ;
            if ( conn.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
            var d = fromOrigin.DistanceTo( conn.Origin ) ;
            var dSq = d * d ;
            if ( dSq <= maxSq ) list.Add( ( conn, dSq ) ) ;
          }
        }
      }
      foreach ( var elem in new FilteredElementCollector( document ).OfClass( typeof( FamilyInstance ) ).WhereElementIsNotElementType().ToElements() )
      {
        if ( elem.Id == excludeElementId ) continue ;
        var elemLevelId = elem.GetLevelId() ;
        if ( elemLevelId != ElementId.InvalidElementId && elemLevelId != levelId ) continue ;
        foreach ( var conn in elem.GetConnectors() )
        {
          if ( conn.Domain != Domain.DomainHvac ) continue ;
          if ( conn.DuctSystemType != DuctSystemType.SupplyAir ) continue ;
          var d = fromOrigin.DistanceTo( conn.Origin ) ;
          var dSq = d * d ;
          if ( dSq <= maxSq ) list.Add( ( conn, dSq ) ) ;
        }
      }
      return list.OrderBy( x => x.DistanceSq ).Select( x => x.Connector ).ToList() ;
    }

    /// <summary>
    /// Thu thập connector HVAC từ MỌI ViewPlan cùng level (vd. HVAC-Floor Plans- 1 Mech). View template khác nhau có thể cho thấy phần tử khác nhau.
    /// </summary>
    private static IReadOnlyList<Connector> CollectSupplyAirConnectorsFromAllPlanViewsOnLevel( Document document, ElementId levelId, XYZ fromOrigin, ElementId excludeElementId )
    {
      if ( levelId == ElementId.InvalidElementId ) return Array.Empty<Connector>() ;
      var list = new List<Connector>() ;
      var viewPlans = new FilteredElementCollector( document ).OfClass( typeof( ViewPlan ) ).Cast<ViewPlan>().Where( v => v.GenLevel?.Id == levelId ).ToList() ;
      foreach ( var vp in viewPlans )
      {
        try
        {
          var fromView = CollectSupplyAirConnectorsInView( document, vp.Id, fromOrigin, excludeElementId ) ;
          list.AddRange( fromView ) ;
        }
        catch
        {
          // Bỏ qua view không đọc được.
        }
      }
      return list ;
    }

    /// <summary>
    /// Thu thập mọi connector HVAC từ các phần tử hiển thị trong view — kể cả chưa gán Duct System để bắt điểm khoanh đỏ.
    /// </summary>
    private static IReadOnlyList<Connector> CollectSupplyAirConnectorsInView( Document document, ElementId viewId, XYZ fromOrigin, ElementId excludeElementId )
    {
      var list = new List<(Connector Connector, double DistanceSq)>() ;
      var elementsInView = new FilteredElementCollector( document, viewId ).WhereElementIsNotElementType().ToElements() ;
      foreach ( var elem in elementsInView )
      {
        if ( elem.Id == excludeElementId ) continue ;
        try
        {
          foreach ( var conn in elem.GetConnectors() )
          {
            if ( conn.Domain != Domain.DomainHvac ) continue ;
            var d = fromOrigin.DistanceTo( conn.Origin ) ;
            list.Add( ( conn, d * d ) ) ;
          }
        }
        catch
        {
          // Bỏ qua phần tử không có ConnectorManager.
        }
      }
      return list.OrderBy( x => x.DistanceSq ).Select( x => x.Connector ).ToList() ;
    }

    protected override IReadOnlyCollection<(string RouteName, RouteSegment Segment)> GetRouteSegments( Document document, AutoRouteState state )
    {
      var (pairs, routeProperty, classificationInfo) = state ;
      RouteGenerator.CorrectEnvelopes( document ) ;

      var systemType = routeProperty.GetSystemType() ;
      var curveType = routeProperty.GetCurveType() ;
      var routes = RouteCache.Get( document ) ;
      var nameBase = systemType?.Name ?? curveType.Category.Name ;
      var nextIndex = GetRouteNameIndex( routes, nameBase ) ;

      var diameter = routeProperty.GetDiameter() ;
      var isRoutingOnPipeSpace = routeProperty.GetRouteOnPipeSpace() ;
      var fromFixedHeight = routeProperty.GetFromFixedHeight() ;
      var toFixedHeight = routeProperty.GetToFixedHeight() ;
      var avoidType = routeProperty.GetAvoidType() ;
      var shaftElementId = routeProperty.GetShaft()?.Id ?? ElementId.InvalidElementId ;

      var segments = new List<(string RouteName, RouteSegment Segment)>( pairs.Count ) ;

      for ( var i = 0 ; i < pairs.Count ; i++ )
      {
        var (fromOut, toGrill) = pairs[ i ] ;
        var fromEndPoint = new ConnectorEndPoint( fromOut, null ) ;
        var toEndPoint = new ConnectorEndPoint( toGrill, null ) ;

        var name = nameBase + "_" + ( nextIndex + i ) ;
        routes.FindOrCreate( name ) ;
        var segment = new RouteSegment(
          classificationInfo,
          systemType,
          curveType,
          fromEndPoint,
          toEndPoint,
          diameter,
          isRoutingOnPipeSpace,
          fromFixedHeight,
          toFixedHeight,
          avoidType,
          shaftElementId ) ;
        segments.Add( ( name, segment ) ) ;
      }

      return segments ;
    }

    private static int GetRouteNameIndex( RouteCache routes, string? targetName )
    {
      var pattern = @"^" + System.Text.RegularExpressions.Regex.Escape( targetName ?? string.Empty ) + @"_(\d+)$" ;
      var regex = new System.Text.RegularExpressions.Regex( pattern ) ;
      var lastIndex = routes.Keys
        .Select( k => regex.Match( k ) )
        .Where( m => m.Success )
        .Select( m => int.Parse( m.Groups[ 1 ].Value ) )
        .Append( 0 )
        .Max() ;
      return lastIndex + 1 ;
    }

    private sealed class AirDistributionBoxSelectionFilter : ISelectionFilter
    {
      public bool AllowElement( Element elem )
      {
        // Cho phép chọn FamilyInstance có ít nhất một connector HVAC SupplyAir.
        if ( elem is not FamilyInstance fi ) return false ;
        return fi.GetConnectors().Any( c =>
          c.Domain == Domain.DomainHvac &&
          c.DuctSystemType == DuctSystemType.SupplyAir ) ;
      }

      public bool AllowReference( Reference reference, XYZ position ) => false ;
    }
  }
}
