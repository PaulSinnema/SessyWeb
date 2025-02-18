﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SessyData.Model;

#nullable disable

namespace SessyData.Migrations
{
    [DbContext(typeof(ModelContext))]
    [Migration("20250218120400_RemoveSolarHistoryEntity")]
    partial class RemoveSolarHistoryEntity
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "9.0.1");

            modelBuilder.Entity("SessyData.Model.EnergyHistory", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<double>("ConsumedTariff1")
                        .HasColumnType("REAL");

                    b.Property<double>("ConsumedTariff2")
                        .HasColumnType("REAL");

                    b.Property<string>("MeterId")
                        .HasColumnType("TEXT");

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
#pragma warning restore 612, 618
        }
    }
}
