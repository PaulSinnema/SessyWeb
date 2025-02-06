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

            modelBuilder.Entity("SessyData.Model.SessyStatusHistory", b =>
                {
                    b.Property<DateTime>("Time")
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .HasColumnType("TEXT");

                    b.Property<string>("Status")
                        .HasColumnType("TEXT");

                    b.Property<string>("StatusDetails")
                        .HasColumnType("TEXT");

                    b.HasKey("Time");

                    b.ToTable("SessyStatusHistory");
                });

            modelBuilder.Entity("SessyData.Model.SolarHistory", b =>
                {
                    b.Property<DateTime>("Time")
                        .HasColumnType("TEXT");

                    b.Property<double>("GeneratedPower")
                        .HasColumnType("REAL");

                    b.Property<double>("GlobalRadiation")
                        .HasColumnType("REAL");

                    b.HasKey("Time");

                    b.ToTable("SolarHistory");
                });
#pragma warning restore 612, 618
        }
    }
}
