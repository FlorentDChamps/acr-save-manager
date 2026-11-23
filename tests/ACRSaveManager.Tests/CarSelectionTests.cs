using System.IO;
using ACRSaveManager.Services;
using ACRSaveManager.Tests.Fixtures;

namespace ACRSaveManager.Tests;

/// <summary>The car of the stage selection (docs §3.4), and the cars of the game.</summary>
public class CarSelectionTests
{
    [Theory]
    [InlineData("LanciaStratosHF")]                  // longer than the fixture's car
    [InlineData("Mini")]                             // shorter
    public void Changes_the_car_and_nothing_else(string car)
    {
        byte[] save = SaveBuilder.PlayerData(online: true, onlineRally: true);
        Assert.Equal(SaveBuilder.WeatherCar, EventList.Find(save).Selection?.Car);

        var write = CarSelection.Replace(save, car);
        CarSelection.Verify(save, write);

        Assert.Equal(SaveBuilder.WeatherCar, write.OldCar);
        Assert.Equal(car, EventList.Find(write.Data).Selection?.Car);
        Assert.Equal(car.Length - SaveBuilder.WeatherCar.Length, write.Data.Length - save.Length);
        Assert.Equal(write.NewSizeField - write.OldSizeField, write.Data.Length - save.Length);

        var before = EventList.Find(save).Selection!;
        var after = EventList.Find(write.Data).Selection!;
        Assert.Equal(before.Stage, after.Stage);
        Assert.Equal(before.Options, after.Options);
        Assert.Equal(before.Start, after.Start);
        Assert.Equal(save[..before.Start], Strip(save, write.Data, after.Start));   // ahead of it, size_field_1 aside
        Assert.Equal(save[before.End..], write.Data[after.End..]);
    }

    /// <summary>The bytes ahead of the selection, size_field_1 set back to the original's.</summary>
    private static byte[] Strip(byte[] original, byte[] written, int end)
    {
        byte[] copy = written[..end];
        int sizeField = SaveFileParser.FindSizeField1Offset(written);
        Buffer.BlockCopy(original, sizeField, copy, sizeField, 4);
        return copy;
    }

    [Fact]
    public void A_new_player_s_save_takes_a_car_too()
    {
        byte[] save = SaveBuilder.PlayerData(newPlayer: true);
        var write = CarSelection.Replace(save, "Peugeot206");
        CarSelection.Verify(save, write);
        Assert.Equal("Peugeot206", EventList.Find(write.Data).Selection?.Car);
    }

    [Fact]
    public void Refuses_what_is_not_a_car_id_and_catches_a_bad_write()
    {
        byte[] save = SaveBuilder.PlayerData();
        Assert.Throws<InvalidDataException>(() => CarSelection.Replace(save, "Lancia Stratos"));

        var write = CarSelection.Replace(save, "LanciaStratosHF");
        byte[] tampered = (byte[])write.Data.Clone();
        tampered[^3] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => CarSelection.Verify(save, write with { Data = tampered }));
        Assert.Throws<InvalidDataException>(() => CarSelection.Verify(save, write with { NewCar = "Peugeot206" }));
    }

    [Fact]
    public void The_catalog_names_the_game_s_cars_by_any_of_their_ids()
    {
        var catalog = GameCatalog.Default;
        Assert.NotEmpty(catalog.Cars);
        Assert.Equal("citroenxsarawrc", catalog.Car("CitroenXsaraWRC")!.Id);
        Assert.Equal(catalog.Car("LanciaRally037Evo2"), catalog.Car("lancia037"));   // an alias
        Assert.Null(catalog.Car("NoSuchCar"));
    }
}
