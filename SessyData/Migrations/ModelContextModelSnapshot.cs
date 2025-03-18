﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SessyData.Model;

#nullable disable

namespace SessyData.Migrations
{
    [DbContext(typeof(ModelContext))]
    partial class ModelContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "9.0.1");

            modelBuilder.Entity("SessyData.Model.EPEXPrices", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<double?>("Price")
                        .HasColumnType("REAL");

                    b.Property<DateTime>("Time")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("EPEXPrices");
                });

            modelBuilder.Entity("SessyData.Model.EnergyHistory", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<double>("ConsumedTariff1")
                        .HasColumnType("REAL");

                    b.Property<double>("ConsumedTariff2")
                        .HasColumnType("REAL");

                    b.Property<double>("GlobalRadiation")
                        .HasColumnType("REAL");

                    b.Property<string>("MeterId")
                        .HasColumnType("TEXT");

                    b.Property<double>("Price")
                        .HasColumnType("REAL");

                    b.Property<double>("ProducedTariff1")
                        .HasColumnType("REAL");

                    b.Property<double>("ProducedTariff2")
                        .HasColumnType("REAL");

                    b.Property<int>("TarrifIndicator")
                        .HasColumnType("INTEGER");

                    b.Property<double>("Temperature")
                        .HasColumnType("REAL");

                    b.Property<DateTime>("Time")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("EnergyHistory");
                });

            modelBuilder.Entity("SessyData.Model.SessyStatusHistory", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("Name")
                        .HasColumnType("TEXT");

                    b.Property<string>("Status")
                        .HasColumnType("TEXT");

                    b.Property<string>("StatusDetails")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("Time")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("SessyStatusHistory");
                });

            modelBuilder.Entity("SessyData.Model.SolarData", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<double>("GlobalRadiation")
                        .HasColumnType("REAL");

                    b.Property<DateTime?>("Time")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("SolarData");
                });

            modelBuilder.Entity("SessyData.Model.Taxes", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<double>("EnergyTax")
                        .HasColumnType("REAL");

                    b.Property<double>("PurchaseCompensation")
                        .HasColumnType("REAL");

                    b.Property<double>("TaxReduction")
                        .HasColumnType("REAL");

                    b.Property<DateTime?>("Time")
                        .HasColumnType("TEXT");

                    b.Property<double>("ValueAddedTax")
                        .HasColumnType("REAL");

                    b.HasKey("Id");

                    b.ToTable("Taxes");
                });
#pragma warning restore 612, 618
        }
    }
}
