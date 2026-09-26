using System.Numerics;

namespace DndMcp.Domain.Dice;

/// <summary>
/// An exact probability or mean, always in lowest terms with a positive denominator, so two equal values compare
/// equal and the text shown to the user ("7/432") is the canonical one.
/// </summary>
public readonly record struct Fraction
{
    private Fraction(BigInteger numerator, BigInteger denominator)
    {
        Numerator = numerator;
        Denominator = denominator;
    }

    public BigInteger Numerator { get; }

    public BigInteger Denominator { get; }

    public static Fraction Create(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new DivideByZeroException("A fraction's denominator cannot be zero.");
        }

        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        return gcd.IsOne || gcd.IsZero ? new Fraction(numerator, denominator) : new Fraction(numerator / gcd, denominator / gcd);
    }

    /// <summary>
    /// Correctly scaled even when both parts are far beyond double's range (an exploding-die distribution has
    /// denominators like 6^101): the quotient is taken to 64 significant bits in integers first, then scaled.
    /// </summary>
    public double ToDouble() => ToDouble(Numerator, Denominator);

    /// <summary>numerator / denominator as a double, without reducing first (for the many-values hot path).</summary>
    public static double ToDouble(BigInteger numerator, BigInteger denominator)
    {
        if (numerator.IsZero)
        {
            return 0;
        }

        var shift = 64 - (int)(BigInteger.Abs(numerator).GetBitLength() - BigInteger.Abs(denominator).GetBitLength());
        var quotient = shift >= 0 ? (numerator << shift) / denominator : numerator / (denominator << -shift);
        return Math.ScaleB((double)quotient, -shift);
    }

    public override string ToString() => Denominator.IsOne ? Numerator.ToString() : $"{Numerator}/{Denominator}";
}
