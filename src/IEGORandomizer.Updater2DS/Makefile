.SUFFIXES:

ifeq ($(strip $(DEVKITARM)),)
$(error "Please set DEVKITARM in your environment. export DEVKITARM=<path to>devkitARM")
endif

TOPDIR ?= $(CURDIR)
include $(DEVKITARM)/3ds_rules

TARGET          := updater_2ds
BUILD           := build
SOURCES         := source
DATA            :=
INCLUDES        := source
ROMFS           := romfs

APP_TITLE       := IEGO Luz Parche v1.2.3
APP_DESCRIPTION := Actualizacion ligera v1.2.3 para 2DS (sin borrar partida)
APP_AUTHOR      := Lynx IEGO Randomizer

ARCH    := -march=armv6k -mtune=mpcore -mfloat-abi=hard -mtp=soft
CFLAGS  := -g -Wall -Wextra -O2 -mword-relocations -ffunction-sections $(ARCH) $(INCLUDE) -D__3DS__
ASFLAGS := -g $(ARCH)
LDFLAGS  = -specs=3dsx.specs -g $(ARCH) -Wl,-Map,$(notdir $*.map)
LIBS    := -lctru -lm
LIBDIRS := $(CTRULIB)

ifneq ($(BUILD),$(notdir $(CURDIR)))

export OUTPUT   := $(CURDIR)/$(TARGET)
export TOPDIR   := $(CURDIR)
export VPATH    := $(foreach dir,$(SOURCES),$(CURDIR)/$(dir))
export DEPSDIR  := $(CURDIR)/$(BUILD)

CFILES          := $(foreach dir,$(SOURCES),$(notdir $(wildcard $(dir)/*.c)))
export LD       := $(CC)
export OFILES_SOURCES := $(CFILES:.c=.o)
export OFILES   := $(OFILES_SOURCES)

export INCLUDE  := $(foreach dir,$(INCLUDES),-I$(CURDIR)/$(dir)) \
                   $(foreach dir,$(LIBDIRS),-I$(dir)/include) \
                   -I$(CURDIR)/$(BUILD)

export LIBPATHS := $(foreach dir,$(LIBDIRS),-L$(dir)/lib)

export _3DSXDEPS := $(OUTPUT).smdh

ifneq ($(wildcard $(TOPDIR)/icon.png),)
export APP_ICON := $(TOPDIR)/icon.png
endif

export _3DSXFLAGS += --smdh=$(CURDIR)/$(TARGET).smdh
ifneq ($(ROMFS),)
export _3DSXFLAGS += --romfs=$(CURDIR)/$(ROMFS)
endif

.PHONY: all clean

all: $(BUILD)
	@$(MAKE) --no-print-directory -C $(BUILD) -f $(CURDIR)/Makefile

$(BUILD):
	@mkdir -p $@

clean:
	@rm -fr $(BUILD) $(TARGET).3dsx $(OUTPUT).smdh $(TARGET).elf $(TARGET).cia

else

$(OUTPUT).3dsx  : $(OUTPUT).elf $(_3DSXDEPS)
$(OUTPUT).elf   : $(OFILES)

-include $(DEPSDIR)/*.d

endif
