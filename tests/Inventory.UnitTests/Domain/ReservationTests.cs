using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Domain.Exceptions;

namespace Inventory.UnitTests.Domain;

public class ReservationTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ProductId = Guid.NewGuid();

    private static Reservation NewReservation(int quantity = 1) =>
        Reservation.Create(ProductId, "user-1", quantity, Now);

    [Fact]
    public void Reservation_NewReservation_IsActive()
    {
        var reservation = NewReservation(quantity: 3);

        Assert.NotEqual(Guid.Empty, reservation.Id);
        Assert.Equal(ProductId, reservation.ProductId);
        Assert.Equal("user-1", reservation.UserId);
        Assert.Equal(3, reservation.Quantity);
        Assert.Equal(ReservationStatus.Active, reservation.Status);
        Assert.Equal(Now, reservation.CreatedAtUtc);
        Assert.Null(reservation.ConfirmedAtUtc);
        Assert.Null(reservation.CancelledAtUtc);
    }

    [Fact]
    public void Reservation_ExpiresAfterTwoMinutes()
    {
        var reservation = NewReservation();

        Assert.Equal(Now.AddMinutes(2), reservation.ExpiresAtUtc);
        Assert.False(reservation.IsExpiredAt(Now.AddMinutes(2).AddTicks(-1)));
        Assert.True(reservation.IsExpiredAt(Now.AddMinutes(2)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reservation_NonPositiveQuantity_Throws(int quantity)
    {
        Assert.Throws<DomainException>(() => NewReservation(quantity));
    }

    [Fact]
    public void Reservation_Create_EmptyProductId_Throws()
    {
        Assert.Throws<DomainException>(() => Reservation.Create(Guid.Empty, "user-1", 1, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Reservation_Create_MissingUserId_Throws(string? userId)
    {
        Assert.Throws<DomainException>(() => Reservation.Create(ProductId, userId!, 1, Now));
    }

    [Fact]
    public void Reservation_Active_PastExpiry_CanStillBeCancelled()
    {
        // Until the sweeper runs it is still Active; cancelling releases the stock just as expiry would.
        var reservation = NewReservation();
        var late = Now.AddMinutes(10);

        reservation.Cancel(late);

        Assert.Equal(ReservationStatus.Cancelled, reservation.Status);
        Assert.Equal(late, reservation.CancelledAtUtc);
    }

    [Fact]
    public void Reservation_FailedTransition_LeavesTimestampsUntouched()
    {
        var reservation = NewReservation();
        reservation.Cancel(Now);

        Assert.Throws<InvalidReservationTransitionException>(() => reservation.Confirm(Now));
        Assert.Throws<InvalidReservationTransitionException>(() => reservation.Expire(Now.AddMinutes(5)));

        Assert.Null(reservation.ConfirmedAtUtc);
        Assert.Equal(Now, reservation.CancelledAtUtc);
    }

    [Fact]
    public void Reservation_Active_CanConfirm()
    {
        var reservation = NewReservation();
        var at = Now.AddMinutes(1);

        reservation.Confirm(at);

        Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
        Assert.Equal(at, reservation.ConfirmedAtUtc);
    }

    [Fact]
    public void Reservation_Active_PastExpiry_CannotConfirm()
    {
        var reservation = NewReservation();

        Assert.Throws<ReservationExpiredException>(() => reservation.Confirm(Now.AddMinutes(2)));
        Assert.Equal(ReservationStatus.Active, reservation.Status);
    }

    [Fact]
    public void Reservation_Active_CanCancel()
    {
        var reservation = NewReservation();
        var at = Now.AddMinutes(1);

        reservation.Cancel(at);

        Assert.Equal(ReservationStatus.Cancelled, reservation.Status);
        Assert.Equal(at, reservation.CancelledAtUtc);
    }

    [Fact]
    public void Reservation_Active_CanExpire()
    {
        var reservation = NewReservation();

        reservation.Expire(Now.AddMinutes(2));

        Assert.Equal(ReservationStatus.Expired, reservation.Status);
    }

    [Fact]
    public void Reservation_Active_BeforeExpiry_CannotExpire()
    {
        var reservation = NewReservation();

        Assert.Throws<DomainException>(() => reservation.Expire(Now.AddMinutes(1)));
        Assert.Equal(ReservationStatus.Active, reservation.Status);
    }

    [Fact]
    public void Reservation_Confirmed_CannotCancel()
    {
        var reservation = NewReservation();
        reservation.Confirm(Now);

        var ex = Assert.Throws<InvalidReservationTransitionException>(() => reservation.Cancel(Now));

        Assert.Equal(ReservationStatus.Confirmed, ex.From);
        Assert.Equal(ReservationStatus.Cancelled, ex.To);
        Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
    }

    [Fact]
    public void Reservation_Cancelled_CannotConfirm()
    {
        var reservation = NewReservation();
        reservation.Cancel(Now);

        Assert.Throws<InvalidReservationTransitionException>(() => reservation.Confirm(Now));
        Assert.Equal(ReservationStatus.Cancelled, reservation.Status);
        Assert.Null(reservation.ConfirmedAtUtc);
    }

    [Fact]
    public void Reservation_Expired_CannotConfirm()
    {
        var reservation = NewReservation();
        reservation.Expire(Now.AddMinutes(2));

        // Same error as confirming an Active-but-overdue reservation, so clients see one consistent answer
        // regardless of whether the expiry worker has run yet.
        Assert.Throws<ReservationExpiredException>(() => reservation.Confirm(Now.AddMinutes(3)));
        Assert.Equal(ReservationStatus.Expired, reservation.Status);
    }

    [Fact]
    public void Reservation_ConfirmAfterExpiry_SameErrorBeforeAndAfterSweep()
    {
        var notYetSwept = NewReservation();
        var swept = NewReservation();
        swept.Expire(Now.AddMinutes(2));

        Assert.Throws<ReservationExpiredException>(() => notYetSwept.Confirm(Now.AddMinutes(3)));
        Assert.Throws<ReservationExpiredException>(() => swept.Confirm(Now.AddMinutes(3)));
    }

    [Fact]
    public void InvalidTransition_RepeatedTransition_SaysAlready()
    {
        var reservation = NewReservation();
        reservation.Confirm(Now);

        var again = Assert.Throws<InvalidReservationTransitionException>(() => reservation.Confirm(Now));
        var cancel = Assert.Throws<InvalidReservationTransitionException>(() => reservation.Cancel(Now));

        Assert.EndsWith("is already Confirmed.", again.Message);
        Assert.EndsWith("is Confirmed and cannot be cancelled.", cancel.Message);
    }

    [Theory]
    [InlineData(ReservationStatus.Confirmed)]
    [InlineData(ReservationStatus.Cancelled)]
    [InlineData(ReservationStatus.Expired)]
    public void Reservation_TerminalState_CannotTransitionAgain(ReservationStatus terminal)
    {
        var later = Now.AddMinutes(5);
        Action<Reservation>[] transitions =
        [
            r => r.Confirm(later),
            r => r.Cancel(later),
            r => r.Expire(later),
        ];

        foreach (var transition in transitions)
        {
            var reservation = NewReservation();
            MoveTo(reservation, terminal);

            // Confirming an Expired reservation is reported as "expired"; every other attempt as an invalid transition.
            var thrown = Assert.ThrowsAny<DomainException>(() => transition(reservation));
            Assert.True(thrown is InvalidReservationTransitionException or ReservationExpiredException, thrown.GetType().Name);
            Assert.Equal(terminal, reservation.Status);
        }
    }

    private static void MoveTo(Reservation reservation, ReservationStatus status)
    {
        switch (status)
        {
            case ReservationStatus.Confirmed: reservation.Confirm(Now); break;
            case ReservationStatus.Cancelled: reservation.Cancel(Now); break;
            case ReservationStatus.Expired: reservation.Expire(Now.AddMinutes(2)); break;
            default: throw new ArgumentOutOfRangeException(nameof(status));
        }
    }
}
