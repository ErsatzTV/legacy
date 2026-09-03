using System.Globalization;
using ErsatzTV.Extensions;

namespace ErsatzTV;

public class HexConstraint : IRouteConstraint
{
    public bool Match(
        HttpContext httpContext,
        IRouter route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        if (values.TryGetValue(routeKey, out object routeValue))
        {
            var stringValue = Convert.ToString(routeValue, CultureInfo.InvariantCulture);
            return stringValue.IsHex();
        }

        return false;
    }
}
